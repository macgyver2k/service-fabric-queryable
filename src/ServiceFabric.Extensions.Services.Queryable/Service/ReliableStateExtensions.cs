using Microsoft.Data.OData;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.OData.Query.SemanticAst;
using Microsoft.Data.Edm;
using System.Linq.Expressions;
using Microsoft.Data.OData.Query;
using ServiceFabric.Extensions.Services.Queryable.Util;
using ServiceFabric.Extensions.Data.Indexing.Persistent;

namespace ServiceFabric.Extensions.Services.Queryable
{
	internal static class ReliableStateExtensions
	{	        
        public static async Task<IEnumerable<TKey>> TryFilterNode<TKey, TValue>(SingleValueNode node, bool notIsApplied, IReliableStateManager stateManager, string dictName, CancellationToken cancellationToken)
            where TKey : IComparable<TKey>, IEquatable<TKey>
        {
            if (node is UnaryOperatorNode asUONode)
            {
                // If NOT, negate tree and return isFilterable
                if (asUONode.OperatorKind == Microsoft.Data.OData.Query.UnaryOperatorKind.Not)
                {
                    if (isFilterableNode2(asUONode.Operand, !notIsApplied))
                    {
                        return await TryFilterNode<TKey, TValue>(asUONode.Operand, !notIsApplied, stateManager, dictName, cancellationToken);
                    }

                    return null;
                }
                else
                {
                    throw new NotSupportedException("Does not support the Negate unary operator");
                }
            }

            else if (node is BinaryOperatorNode asBONode)
            {
                // Filterable(A) AND Filterable(B)      => Intersect(Filter(A), Filter(B))
                // !Filterable(A) AND Filterable(B)     => Filter(B)
                // Filterable(A) AND !Filterable(B)     => Filter(A)
                // !Filterable(A) AND !Filterable(B)    => NF
                if ((asBONode.OperatorKind == BinaryOperatorKind.And && !notIsApplied) ||
                    (asBONode.OperatorKind == BinaryOperatorKind.Or && notIsApplied))
                {
                    bool leftFilterable = isFilterableNode2(asBONode.Left, notIsApplied);
                    bool rightFilterable = isFilterableNode2(asBONode.Right, notIsApplied);

                    // Both are filterable: intersect
                    if (leftFilterable && rightFilterable)
                    {
                        IEnumerable<TKey> leftKeys = await TryFilterNode<TKey, TValue>(asBONode.Left, notIsApplied, stateManager, dictName, cancellationToken);
                        IEnumerable<TKey> rightKeys = await TryFilterNode<TKey, TValue>(asBONode.Right, notIsApplied, stateManager, dictName, cancellationToken);

                        if (leftKeys != null && rightKeys != null)
                        {
                            return new IEnumerableUtility.IntersectEnumerable<TKey>(leftKeys, rightKeys);
                        }
                        else if (leftKeys != null)
                        {
                            return await TryFilterNode<TKey, TValue>(asBONode.Left, notIsApplied, stateManager, dictName, cancellationToken);
                        }
                        else if (rightKeys != null)
                        {
                            return await TryFilterNode<TKey, TValue>(asBONode.Right, notIsApplied, stateManager, dictName, cancellationToken);
                        }
                        else
                        {
                            return null; //Both queries were candidates for filtering but the filterable indexes did not exist
                        }
                    }
                    else if (leftFilterable)
                    {
                        return await TryFilterNode<TKey, TValue>(asBONode.Left, notIsApplied, stateManager, dictName, cancellationToken);
                    }
                    else if (rightFilterable)
                    {
                        return await TryFilterNode<TKey, TValue>(asBONode.Right, notIsApplied, stateManager, dictName, cancellationToken);
                    }
                    else
                    {
                        return null; // This should never be hit because if !Filterable(Left) && !Filterable(Right) => !Filterable(Me)
                    }

                }
                // Filterable(A) OR Filterable(B)      => Union(Filter(A), Filter(B))
                // !Filterable(A) OR Filterable(B)     => NF
                // Filterable(A) OR !Filterable(B)     => NF
                // !Filterable(A) OR !Filterable(B)    => NF
                else if ((asBONode.OperatorKind == BinaryOperatorKind.Or && !notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.And && notIsApplied))
                {
                    bool leftFilterable = isFilterableNode2(asBONode.Left, notIsApplied);
                    bool rightFilterable = isFilterableNode2(asBONode.Right, notIsApplied);

                    // Both are filterable queries: intersect, however if they are null that means there is no index for this property
                    if (leftFilterable && rightFilterable)
                    {
                        IEnumerable<TKey> leftKeys = await TryFilterNode<TKey, TValue>(asBONode.Left, notIsApplied, stateManager, dictName, cancellationToken);
                        IEnumerable<TKey> rightKeys = await TryFilterNode<TKey, TValue>(asBONode.Right, notIsApplied, stateManager, dictName, cancellationToken);

                        if (leftKeys != null && rightKeys != null)
                        {
                            return new IEnumerableUtility.UnionEnumerable<TKey>(leftKeys, rightKeys);
                        }
                    }

                    return null;
                }
                // If Equals, >=, >, <, <=
                else if ((asBONode.OperatorKind == BinaryOperatorKind.Equal && !notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.NotEqual && notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.GreaterThan) || 
                         (asBONode.OperatorKind == BinaryOperatorKind.GreaterThanOrEqual) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.LessThan) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.LessThanOrEqual))
                {
                    // Resolve the arbitrary order of the request
                    SingleValuePropertyAccessNode valueNode = asBONode.Left is SingleValuePropertyAccessNode ? asBONode.Left as SingleValuePropertyAccessNode : asBONode.Right as SingleValuePropertyAccessNode;
                    ConstantNode constantNode = asBONode.Left is ConstantNode ? asBONode.Left as ConstantNode : asBONode.Right as ConstantNode;

                    // If constant node is LEFT and AccessNode is RIGHT, we should flip the OperatorKind to standardize to "access operator constant"
                    // ie 21 gt Age is logical opposite of Age lt 21
                    BinaryOperatorKind operatorKind = asBONode.OperatorKind;
                    if (asBONode.Left is ConstantNode)
                    {
                        if (operatorKind == BinaryOperatorKind.GreaterThan)
                            operatorKind = BinaryOperatorKind.LessThan;
                        else if (operatorKind == BinaryOperatorKind.LessThan)
                            operatorKind = BinaryOperatorKind.GreaterThan;
                        else if (operatorKind == BinaryOperatorKind.LessThanOrEqual)
                            operatorKind = BinaryOperatorKind.GreaterThanOrEqual;
                        else if (operatorKind == BinaryOperatorKind.GreaterThanOrEqual)
                            operatorKind = BinaryOperatorKind.LessThanOrEqual;
                    }

                    string propertyName = valueNode.Property.Name;
                    Type propertyType = constantNode.Value.GetType(); //Possible reliance on type bad if name of property and provided type conflict?

                    MethodInfo getIndexedDictionaryByPropertyName = typeof(ReliableStateExtensions).GetMethod("GetIndexedDictionaryByPropertyName", BindingFlags.NonPublic | BindingFlags.Static);
                    getIndexedDictionaryByPropertyName = getIndexedDictionaryByPropertyName.MakeGenericMethod(new Type[] { typeof(TKey), typeof(TValue), propertyType });
                    Task indexedDictTask = (Task)getIndexedDictionaryByPropertyName.Invoke(null, new object[] { stateManager, dictName, propertyName });
                    await indexedDictTask;
                    var indexedDict = indexedDictTask.GetType().GetProperty("Result").GetValue(indexedDictTask);

                    if (indexedDict == null)
                    {
                        return null; // Filter does not exist or dictionary does not exist
                    }

                    MethodInfo filterHelper = typeof(ReliableStateExtensions).GetMethod("FilterHelper", BindingFlags.Public | BindingFlags.Static);
                    filterHelper = filterHelper.MakeGenericMethod(new Type[] { typeof(TKey), typeof(TValue), propertyType });
                    Task filterHelperTask = (Task)filterHelper.Invoke(null, new object[] { indexedDict, constantNode.Value, operatorKind, notIsApplied, cancellationToken, stateManager, propertyName });
                    await filterHelperTask;
                    return (IEnumerable<TKey>)filterHelperTask.GetType().GetProperty("Result").GetValue(filterHelperTask);
                }
                // We choose to mark NotEquals as unfilterable. Theoretically with indexes with low number of keys may be slightly faster than not-filtering
                // But generally is same order of magnitude as not using filters
                else if ((asBONode.OperatorKind == BinaryOperatorKind.Equal && notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.NotEqual && !notIsApplied))
                {
                    return null;
                }
                else
                {
                    throw new NotSupportedException("Does not support Add, Subtract, Modulo, Multiply, Divide operations.");
                }
            }
            else if (node is ConvertNode asCNode)
            {
                return await TryFilterNode<TKey, TValue>(asCNode.Source, notIsApplied, stateManager, dictName, cancellationToken);
            }
            else
            {
                throw new NotSupportedException("Only supports Binary and Unary operator nodes");
            }
        }

      

        private static bool isFilterableNode2(SingleValueNode node, bool notIsApplied)
        {
            if (node is UnaryOperatorNode asUONode)
            {
                // If NOT, negate tree and return isFilterable
                if (asUONode.OperatorKind == UnaryOperatorKind.Not)
                {
                    return isFilterableNode2(asUONode.Operand, !notIsApplied);
                }
                else
                {
                    throw new NotSupportedException("Does not support the Negate unary operator");
                }
            }
            else if (node is BinaryOperatorNode asBONode)
            {
                // Filterable(A) AND Filterable(B)      => Intersect(Filter(A), Filter(B))
                // !Filterable(A) AND Filterable(B)     => Filter(B)
                // Filterable(A) AND !Filterable(B)     => Filter(A)
                // !Filterable(A) AND !Filterable(B)    => NF
                if ((asBONode.OperatorKind == BinaryOperatorKind.And && !notIsApplied) ||
                    (asBONode.OperatorKind == BinaryOperatorKind.Or && notIsApplied))
                {
                    if (!isFilterableNode2(asBONode.Left, notIsApplied) && !isFilterableNode2(asBONode.Right, notIsApplied))
                    {
                        return false;
                    }

                    return true;
                }
                // Filterable(A) OR Filterable(B)      => Union(Filter(A), Filter(B))
                // !Filterable(A) OR Filterable(B)     => NF
                // Filterable(A) OR !Filterable(B)     => NF
                // !Filterable(A) OR !Filterable(B)    => NF
                else if ((asBONode.OperatorKind == BinaryOperatorKind.Or && !notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.And && notIsApplied))
                {
                    if (isFilterableNode2(asBONode.Left, notIsApplied) && isFilterableNode2(asBONode.Right, notIsApplied))
                    {
                        return true;
                    }

                    return false;
                }
                // If Equals, >=, >, <, <=
                else if ((asBONode.OperatorKind == BinaryOperatorKind.Equal && !notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.NotEqual && notIsApplied) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.GreaterThan) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.GreaterThanOrEqual) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.LessThan) ||
                         (asBONode.OperatorKind == BinaryOperatorKind.LessThanOrEqual))
                {
                    return true;
                }
                // We choose to mark NotEquals as unfilterable. Theoretically with indexes with low number of keys may be slightly faster than not-filtering
                // But generally is same order of magnitude as not using filters
                else if ((asBONode.OperatorKind == Microsoft.Data.OData.Query.BinaryOperatorKind.Equal && notIsApplied) ||
                         (asBONode.OperatorKind == Microsoft.Data.OData.Query.BinaryOperatorKind.NotEqual && !notIsApplied))
                {
                    return false;
                }
                else
                {
                    throw new NotSupportedException("Does not support Add, Subtract, Modulo, Multiply, Divide operations.");
                }
            }
            else if (node is ConvertNode asCNode)
            {
                return isFilterableNode2(asCNode.Source, notIsApplied);
            }
            else
            {
                throw new NotSupportedException("Only supports Binary and Unary operator nodes");
            }
        } 
	
		/// <summary>
		/// Get the Entity model type from the reliable dictionary.
		/// This is the full metadata type definition for the rows in the
		/// dictionary (key, value, partition, etag).
		/// </summary>
		/// <param name="state">Reliable dictionary instance.</param>
		/// <returns>The Entity model type for the dictionary.</returns>
		private static Type GetEntityType(this IReliableState state)
		{
			var keyType = state.GetKeyType();
			var valueType = state.GetValueType();
			return typeof(Entity<,>).MakeGenericType(keyType, valueType);
		}

		/// <summary>
		/// Get the key type from the reliable dictionary.
		/// </summary>
		/// <param name="state">Reliable dictionary instance.</param>
		/// <returns>The type of the dictionary's keys.</returns>
		private static Type GetKeyType(this IReliableState state)
		{
			if (!state.ImplementsGenericType(typeof(IReliableDictionary<,>)))
				throw new ArgumentException(nameof(state));

			return state.GetType().GetGenericArguments()[0];
		}

		/// <summary>
		/// Get the value type from the reliable dictionary.
		/// </summary>
		/// <param name="state">Reliable dictionary instance.</param>
		/// <returns>The type of the dictionary's values.</returns>
		private static Type GetValueType(this IReliableState state)
		{
			if (!state.ImplementsGenericType(typeof(IReliableDictionary<,>)))
				throw new ArgumentException(nameof(state));

			return state.GetType().GetGenericArguments()[1];
		}		
	}
}