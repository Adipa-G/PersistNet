using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace PersistNet;

/// <summary>
/// Represents a deferred entity fetch that supports fluent eager-loading declarations
/// before the query is executed.
/// </summary>
public interface IEntityQuery<T> where T : class
{
    /// <summary>
    /// Adds a single navigation property to eagerly load. Can be chained multiple times.
    /// </summary>
    IEntityQuery<T> Include(Expression<Func<T, object?>> navigation);

    /// <summary>
    /// Eagerly loads the entire reachable relationship graph from the root entity.
    /// </summary>
    IEntityQuery<T> IncludeAll();

    /// <summary>Makes the object directly awaitable with <c>await</c>.</summary>
    TaskAwaiter<T> GetAwaiter();

    /// <summary>
    /// Returns the underlying <see cref="Task{T}"/> for use in <c>Func&lt;Task&gt;</c>
    /// contexts such as <c>Assert.ThrowsAsync</c>.
    /// </summary>
    Task<T> AsTask();
}
