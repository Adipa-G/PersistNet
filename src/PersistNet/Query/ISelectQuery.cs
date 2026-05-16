using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.Query;

/// <summary>
/// Fluent query builder returned by <see cref="ITransaction.Query{T}"/>.
/// All methods return <c>this</c> to allow chaining. SQL is compiled and executed
/// only when a terminal method (<see cref="ToListAsync"/>, <see cref="CountAsync"/>,
/// etc.) is called.
/// </summary>
/// <typeparam name="T">
/// An entity type decorated with <see cref="TableInfo"/>. Must have a public
/// parameterless constructor.
/// </typeparam>
public interface ISelectQuery<T> where T : class, new()
{
    // ── Filtering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a WHERE condition expressed as a C# lambda predicate.
    /// Supported: ==, !=, &lt;, &gt;, &lt;=, &gt;=, &amp;&amp;, ||, !,
    /// <c>string.Contains/StartsWith/EndsWith</c>, and <c>collection.Contains(p.Field)</c>.
    /// Multiple calls are combined with AND.
    /// For operations not expressible as lambdas (Between, In, sub-queries) use the
    /// <see cref="Where(IConditionExpr)"/> overload.
    /// </summary>
    ISelectQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Adds a WHERE condition built with the fluent expression builder
    /// (<see cref="Expr.Field{T}(Expression{Func{T, object?}})"/> etc.).
    /// Multiple calls are combined with AND.
    /// </summary>
    ISelectQuery<T> Where(IConditionExpr condition);

    // ── Ordering ──────────────────────────────────────────────────────────────

    /// <summary>Sorts by the specified column ascending.</summary>
    ISelectQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Sorts by the specified column descending.</summary>
    ISelectQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Adds a secondary ascending sort. Only valid after <see cref="OrderBy{TKey}"/> or <see cref="OrderByDescending{TKey}"/>.</summary>
    ISelectQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Adds a secondary descending sort.</summary>
    ISelectQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    // ── Pagination ────────────────────────────────────────────────────────────

    /// <summary>Limits the result to at most <paramref name="count"/> rows.</summary>
    ISelectQuery<T> Take(int count);

    /// <summary>Skips the first <paramref name="count"/> rows (OFFSET).</summary>
    ISelectQuery<T> Skip(int count);

    // ── Terminals ─────────────────────────────────────────────────────────────

    /// <summary>Executes the query and returns all matching rows.</summary>
    Task<IReadOnlyList<T>> ToListAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes the query and returns the first matching row, or <c>null</c> if
    /// no rows match.
    /// </summary>
    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);

    /// <summary>Returns the number of rows matching the current filters.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if any row matches the current filters; <c>false</c>
    /// otherwise.
    /// </summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if any row matches <paramref name="predicate"/> (combined
    /// with any previously set filters); <c>false</c> otherwise.
    /// </summary>
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    /// <summary>Returns the sum of <paramref name="selector"/> across all matching rows.</summary>
    Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct;

    /// <summary>Returns the maximum value of <paramref name="selector"/> across all matching rows, or <c>null</c> if no rows match.</summary>
    Task<TResult?> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct;

    /// <summary>Returns the minimum value of <paramref name="selector"/> across all matching rows, or <c>null</c> if no rows match.</summary>
    Task<TResult?> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct;
}
