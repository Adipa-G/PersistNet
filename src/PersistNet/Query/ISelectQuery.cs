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

    /// <summary>
    /// Adds a raw SQL WHERE fragment — an escape hatch for conditions the builder cannot
    /// express.  Parameters use <c>@name</c> syntax and are supplied as an anonymous
    /// object (<c>new { lo = 10, hi = 100 }</c>) or a <c>Dictionary&lt;string,object?&gt;</c>.
    /// </summary>
    ISelectQuery<T> Where(string rawSql, object? parameters = null);

    /// <summary>
    /// Adds a WHERE condition on a joined entity type.
    /// Only valid after calling <see cref="InnerJoin{TJoin}"/> or <see cref="LeftJoin{TJoin}"/>.
    /// </summary>
    ISelectQuery<T> Where<TJoin>(Expression<Func<TJoin, bool>> predicate) where TJoin : class;

    // ── Joins ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an INNER JOIN to <typeparamref name="TJoin"/>.
    /// The two-parameter lambda specifies the join condition:
    /// <c>(t, j) => t.ForeignKeyId == j.Id</c>.
    /// The query still returns <typeparamref name="T"/> rows filtered by the join.
    /// </summary>
    ISelectQuery<T> InnerJoin<TJoin>(Expression<Func<T, TJoin, bool>> condition) where TJoin : class, new();

    /// <summary>
    /// Adds a LEFT JOIN to <typeparamref name="TJoin"/>.
    /// Rows from the primary table are always included; joined columns are NULL when
    /// there is no matching row in <typeparamref name="TJoin"/>.
    /// </summary>
    ISelectQuery<T> LeftJoin<TJoin>(Expression<Func<T, TJoin, bool>> condition) where TJoin : class, new();

    // ── Grouping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a GROUP BY column. Multiple calls produce a multi-column GROUP BY.
    /// </summary>
    ISelectQuery<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>
    /// Adds a raw SQL GROUP BY fragment (escape hatch).
    /// </summary>
    ISelectQuery<T> GroupBy(string rawSql);

    // ── Having ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a HAVING condition as a lambda predicate.
    /// Typically combined with aggregate expressions via <see cref="Expr"/>.
    /// </summary>
    ISelectQuery<T> Having(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Adds a HAVING condition built with the fluent expression builder, including
    /// aggregate expressions such as <c>Expr.Count().Gt().Value(2)</c>.
    /// </summary>
    ISelectQuery<T> Having(IConditionExpr condition);

    /// <summary>
    /// Adds a raw SQL HAVING fragment (escape hatch).
    /// Parameters use <c>@name</c> syntax; supply via anonymous object or dictionary.
    /// </summary>
    ISelectQuery<T> Having(string rawSql, object? parameters = null);

    // ── Ordering ──────────────────────────────────────────────────────────────

    /// <summary>Sorts by the specified column ascending.</summary>
    ISelectQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Sorts by the specified column descending.</summary>
    ISelectQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Adds a secondary ascending sort. Only valid after <see cref="OrderBy{TKey}"/> or <see cref="OrderByDescending{TKey}"/>.</summary>
    ISelectQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Adds a secondary descending sort.</summary>
    ISelectQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>
    /// Appends a raw SQL ORDER BY fragment (escape hatch for functions, expressions, etc.).
    /// </summary>
    ISelectQuery<T> OrderBy(string rawSql);

    // ── Deduplication ─────────────────────────────────────────────────────────

    /// <summary>
    /// Emits <c>SELECT DISTINCT</c>, removing duplicate rows from the result set.
    /// </summary>
    ISelectQuery<T> Distinct();

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

    /// <summary>
    /// Returns the average of <paramref name="selector"/> across all matching rows as a
    /// <c>double</c>, or <c>null</c> if no rows match. SQL AVG always produces a
    /// floating-point result regardless of the column type.
    /// </summary>
    Task<double?> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct;

    // ── Projection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Switches the result type to <typeparamref name="TDto"/> by selecting only the
    /// columns declared on that DTO via <see cref="ColumnInfo"/> attributes.
    /// Returns an <see cref="ISelectProjectedQuery{TDto}"/> on which ordering,
    /// pagination, and terminals can be chained.
    /// </summary>
    /// <remarks>
    /// Structural modifiers (<see cref="Where(System.Linq.Expressions.Expression{System.Func{T, bool}})"/>,
    /// <see cref="InnerJoin{TJoin}"/>, <see cref="GroupBy{TKey}"/>, etc.) must be
    /// called before <c>Select</c>; ordering and pagination can be called either
    /// before or after.
    /// </remarks>
    ISelectProjectedQuery<TDto> Select<TDto>() where TDto : class, new();
}

