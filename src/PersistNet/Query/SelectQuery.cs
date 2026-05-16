using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PersistNet.DbAbstraction;

namespace PersistNet.Query;

/// <summary>
/// Default implementation of <see cref="ISelectQuery{T}"/>. Accumulates clauses and
/// delegates SQL compilation + execution to <see cref="SelectQueryCompiler"/> and
/// <see cref="IDbPersistence"/> on each terminal call.
/// </summary>
internal sealed class SelectQuery<T> : ISelectQuery<T> where T : class, new()
{
    private readonly IDbPersistence _persistence;
    private readonly List<IConditionExpr> _whereClauses = [];
    private readonly List<(PropertyInfo Prop, bool Desc)> _orderBy = [];
    private int? _take;
    private int? _skip;

    internal SelectQuery(IDbPersistence persistence)
        => _persistence = persistence;

    // ── Filtering ─────────────────────────────────────────────────────────

    public ISelectQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _whereClauses.Add(new LambdaConditionExpr(predicate));
        return this;
    }

    public ISelectQuery<T> Where(IConditionExpr condition)
    {
        _whereClauses.Add(condition);
        return this;
    }

    // ── Ordering ──────────────────────────────────────────────────────────

    public ISelectQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy.Add((ExtractProp(keySelector), false));
        return this;
    }

    public ISelectQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _orderBy.Add((ExtractProp(keySelector), true));
        return this;
    }

    public ISelectQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => OrderBy(keySelector);

    public ISelectQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        => OrderByDescending(keySelector);

    // ── Pagination ────────────────────────────────────────────────────────

    public ISelectQuery<T> Take(int count) { _take = count; return this; }
    public ISelectQuery<T> Skip(int count) { _skip = count; return this; }

    // ── Terminals ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileSelect<T>(
            _whereClauses, _orderBy, _skip, _take, _persistence.Quote,
            _persistence.AppendLimitOffset);
        return _persistence.ExecuteQueryAsync<T>(sql, parms, ct);
    }

    public async Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileSelect<T>(
            _whereClauses, _orderBy, _skip, take: 1, _persistence.Quote,
            _persistence.AppendLimitOffset);
        var rows = await _persistence.ExecuteQueryAsync<T>(sql, parms, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileCount<T>(
            _whereClauses, _persistence.Quote);
        return _persistence.ExecuteScalarAsync<int>(sql, parms, ct);
    }

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => AnyAsync(null, ct);

    public async Task<bool> AnyAsync(Expression<Func<T, bool>>? predicate, CancellationToken ct = default)
    {
        var clauses = new List<IConditionExpr>(_whereClauses);
        if (predicate is not null) clauses.Add(new LambdaConditionExpr(predicate));

        var (sql, parms) = SelectQueryCompiler.CompileCount<T>(clauses, _persistence.Quote);
        var count = await _persistence.ExecuteScalarAsync<int>(sql, parms, ct);
        return count > 0;
    }

    public async Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "SUM", selector, _whereClauses, _persistence.Quote);
        var result = await _persistence.ExecuteScalarAsync<TResult>(sql, parms, ct);
        return result;
    }

    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "MAX", selector, _whereClauses, _persistence.Quote);
        return await _persistence.ExecuteScalarNullableAsync<TResult>(sql, parms, ct);
    }

    public async Task<TResult?> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "MIN", selector, _whereClauses, _persistence.Quote);
        return await _persistence.ExecuteScalarNullableAsync<TResult>(sql, parms, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PropertyInfo ExtractProp<TKey>(Expression<Func<T, TKey>> expr)
    {
        var body = expr.Body;
        if (body is UnaryExpression u && u.NodeType == System.Linq.Expressions.ExpressionType.Convert)
            body = u.Operand;
        if (body is MemberExpression m && m.Member is PropertyInfo pi)
            return pi;
        throw new ArgumentException(
            $"Key selector must be a simple property accessor (e.g. p => p.Name). Got: {expr.Body}",
            nameof(expr));
    }
}
