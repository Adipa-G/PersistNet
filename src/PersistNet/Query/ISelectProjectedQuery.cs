using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.Query;

/// <summary>
/// A read-only query that projects results into <typeparamref name="TDto"/> using
/// the DTO's <see cref="ColumnInfo"/>-decorated properties. Returned by
/// <see cref="ISelectQuery{T}.Select{TDto}"/>.
/// </summary>
/// <typeparam name="TDto">
/// A class whose public properties carry <see cref="ColumnInfo"/> attributes
/// describing which DB columns to include in the projection. Does not require
/// a <see cref="TableInfo"/> attribute. Must have a public parameterless constructor.
/// </typeparam>
public interface ISelectProjectedQuery<TDto> where TDto : class, new()
{
    // ── Ordering ──────────────────────────────────────────────────────────

    /// <summary>Sorts the projected results by the specified DTO property ascending.</summary>
    ISelectProjectedQuery<TDto> OrderBy<TKey>(Expression<Func<TDto, TKey>> keySelector);

    /// <summary>Sorts the projected results by the specified DTO property descending.</summary>
    ISelectProjectedQuery<TDto> OrderByDescending<TKey>(Expression<Func<TDto, TKey>> keySelector);

    /// <summary>Adds a secondary ascending sort.</summary>
    ISelectProjectedQuery<TDto> ThenBy<TKey>(Expression<Func<TDto, TKey>> keySelector);

    /// <summary>Adds a secondary descending sort.</summary>
    ISelectProjectedQuery<TDto> ThenByDescending<TKey>(Expression<Func<TDto, TKey>> keySelector);

    /// <summary>Appends a raw SQL ORDER BY fragment (escape hatch).</summary>
    ISelectProjectedQuery<TDto> OrderBy(string rawSql);

    // ── Pagination ────────────────────────────────────────────────────────

    /// <summary>Limits the result to at most <paramref name="count"/> rows.</summary>
    ISelectProjectedQuery<TDto> Take(int count);

    /// <summary>Skips the first <paramref name="count"/> rows (OFFSET).</summary>
    ISelectProjectedQuery<TDto> Skip(int count);

    // ── Deduplication ─────────────────────────────────────────────────────

    /// <summary>Emits <c>SELECT DISTINCT</c>, removing duplicate projected rows.</summary>
    ISelectProjectedQuery<TDto> Distinct();

    // ── Terminals ─────────────────────────────────────────────────────────

    /// <summary>Executes the query and returns all projected rows.</summary>
    Task<IReadOnlyList<TDto>> ToListAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes the query and returns the first projected row, or <c>null</c> if
    /// no rows match.
    /// </summary>
    Task<TDto?> FirstOrDefaultAsync(CancellationToken ct = default);
}
