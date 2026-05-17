using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PersistNet.DbAbstraction;

namespace PersistNet.Query;

/// <summary>
/// Default implementation of <see cref="ISelectProjectedQuery{TDto}"/>.
/// Wraps a completed <see cref="SelectQuery{T}"/> and adds DTO-specific ordering,
/// pagination, and deduplication state before delegating compilation to
/// <see cref="SelectQueryCompiler.CompileSelectProjected{T,TDto}"/>.
/// </summary>
internal sealed class SelectProjectedQuery<T, TDto> : ISelectProjectedQuery<TDto>
    where T : class, new()
    where TDto : class, new()
{
    private readonly IDbPersistence                        _persistence;
    private readonly SelectQuery<T>                        _parent;
    private readonly List<(PropertyInfo Prop, bool Desc)>  _orderBy    = [];
    private readonly List<string>                          _orderByRaw = [];
    private int?  _take;
    private int?  _skip;
    private bool  _distinct;

    internal SelectProjectedQuery(IDbPersistence persistence, SelectQuery<T> parent)
    {
        _persistence = persistence;
        _parent      = parent;
    }

    // ── Ordering ──────────────────────────────────────────────────────────

    public ISelectProjectedQuery<TDto> OrderBy<TKey>(Expression<Func<TDto, TKey>> keySelector)
    {
        _orderBy.Add((ExtractProp(keySelector), false));
        return this;
    }

    public ISelectProjectedQuery<TDto> OrderByDescending<TKey>(Expression<Func<TDto, TKey>> keySelector)
    {
        _orderBy.Add((ExtractProp(keySelector), true));
        return this;
    }

    public ISelectProjectedQuery<TDto> ThenBy<TKey>(Expression<Func<TDto, TKey>> keySelector)
        => OrderBy(keySelector);

    public ISelectProjectedQuery<TDto> ThenByDescending<TKey>(Expression<Func<TDto, TKey>> keySelector)
        => OrderByDescending(keySelector);

    public ISelectProjectedQuery<TDto> OrderBy(string rawSql)
    {
        _orderByRaw.Add(rawSql);
        return this;
    }

    // ── Pagination ────────────────────────────────────────────────────────

    public ISelectProjectedQuery<TDto> Take(int count) { _take = count; return this; }
    public ISelectProjectedQuery<TDto> Skip(int count) { _skip = count; return this; }

    // ── Deduplication ─────────────────────────────────────────────────────

    public ISelectProjectedQuery<TDto> Distinct() { _distinct = true; return this; }

    // ── Terminals ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<TDto>> ToListAsync(CancellationToken ct = default)
    {
        var (sql, parms) = Compile(
            skip: _skip ?? _parent.SkipRows,
            take: _take ?? _parent.TakeRows);
        return _persistence.ExecuteQueryAsync<TDto>(sql, parms, ct);
    }

    public async Task<TDto?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var (sql, parms) = Compile(
            skip: _skip ?? _parent.SkipRows,
            take: 1);
        var rows = await _persistence.ExecuteQueryAsync<TDto>(sql, parms, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private (string Sql, List<(string, object?)> Parameters) Compile(int? skip, int? take)
        => SelectQueryCompiler.CompileSelectProjected<T, TDto>(
            _parent.WhereClauses,
            _parent.Joins,
            _parent.GroupByFields,
            _parent.GroupByRawList,
            _parent.HavingClauses,
            _parent.OrderByList,
            _parent.OrderByRawList,
            _orderBy,
            _orderByRaw,
            skip, take,
            _distinct || _parent.DistinctFlag,
            _persistence.Quote,
            _persistence.AppendLimitOffset);

    private static PropertyInfo ExtractProp<TKey>(Expression<Func<TDto, TKey>> expr)
    {
        var body = expr.Body;
        if (body is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            body = u.Operand;
        if (body is MemberExpression m && m.Member is PropertyInfo pi)
            return pi;
        throw new ArgumentException(
            $"Key selector must be a simple property accessor (e.g. dto => dto.Name). Got: {expr.Body}",
            nameof(expr));
    }
}
