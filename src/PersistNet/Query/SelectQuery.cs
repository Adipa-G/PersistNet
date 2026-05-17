using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;
using PersistNet.Mapping;

namespace PersistNet.Query;

/// <summary>
/// Default implementation of <see cref="ISelectQuery{T}"/>. Accumulates clauses and
/// delegates SQL compilation + execution to <see cref="SelectQueryCompiler"/> and
/// <see cref="IDbPersistence"/> on each terminal call.
/// </summary>
internal sealed class SelectQuery<T> : ISelectQuery<T> where T : class, new()
{
    private readonly IDbPersistence                          _persistence;
    private readonly List<IConditionExpr>                    _whereClauses  = [];
    private readonly List<(PropertyInfo Prop, bool Desc)>    _orderBy       = [];
    private readonly List<string>                            _orderByRaw    = [];
    private readonly List<JoinClause>                        _joins         = [];
    private readonly List<PropertyInfo>                      _groupByFields = [];
    private readonly List<string>                            _groupByRaw    = [];
    private readonly List<IConditionExpr>                    _havingClauses = [];
    private int? _take;
    private int? _skip;
    private bool _distinct;

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

    public ISelectQuery<T> Where(string rawSql, object? parameters = null)
    {
        var parms = DtoMapper.ExtractParameters(parameters);
        _whereClauses.Add(new RawSqlConditionExpr(rawSql, parms));
        return this;
    }

    public ISelectQuery<T> Where<TJoin>(Expression<Func<TJoin, bool>> predicate) where TJoin : class
    {
        _whereClauses.Add(new LambdaConditionExpr(predicate, typeof(TJoin)));
        return this;
    }

    // ── Joins ─────────────────────────────────────────────────────────────

    public ISelectQuery<T> InnerJoin<TJoin>(Expression<Func<T, TJoin, bool>> condition) where TJoin : class, new()
        => AddJoin<TJoin>(condition, "INNER JOIN");

    public ISelectQuery<T> LeftJoin<TJoin>(Expression<Func<T, TJoin, bool>> condition) where TJoin : class, new()
        => AddJoin<TJoin>(condition, "LEFT JOIN");

    private ISelectQuery<T> AddJoin<TJoin>(LambdaExpression condition, string keyword) where TJoin : class, new()
    {
        var table = DbInfoCache.FindTable(typeof(TJoin))
            ?? throw new InvalidOperationException(
                $"Type '{typeof(TJoin).Name}' is not registered as a table entity. " +
                "Ensure it has a [TableInfo] attribute.");
        _joins.Add(new JoinClause(typeof(TJoin), table, condition, keyword));
        return this;
    }

    // ── Deduplication ─────────────────────────────────────────────────────

    public ISelectQuery<T> Distinct() { _distinct = true; return this; }

    // ── Grouping ──────────────────────────────────────────────────────────

    public ISelectQuery<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        _groupByFields.Add(ExtractProp(keySelector));
        return this;
    }

    public ISelectQuery<T> GroupBy(string rawSql)
    {
        _groupByRaw.Add(rawSql);
        return this;
    }

    // ── Having ────────────────────────────────────────────────────────────

    public ISelectQuery<T> Having(Expression<Func<T, bool>> predicate)
    {
        _havingClauses.Add(new LambdaConditionExpr(predicate));
        return this;
    }

    public ISelectQuery<T> Having(IConditionExpr condition)
    {
        _havingClauses.Add(condition);
        return this;
    }

    public ISelectQuery<T> Having(string rawSql, object? parameters = null)
    {
        var parms = DtoMapper.ExtractParameters(parameters);
        _havingClauses.Add(new RawSqlConditionExpr(rawSql, parms));
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

    public ISelectQuery<T> OrderBy(string rawSql)
    {
        _orderByRaw.Add(rawSql);
        return this;
    }

    // ── Pagination ────────────────────────────────────────────────────────

    public ISelectQuery<T> Take(int count) { _take = count; return this; }
    public ISelectQuery<T> Skip(int count) { _skip = count; return this; }

    // ── Terminals ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileSelect<T>(
            _whereClauses, _orderBy, _orderByRaw, _joins,
            _groupByFields, _groupByRaw, _havingClauses,
            _skip, _take, _distinct, _persistence.Quote, _persistence.AppendLimitOffset);
        return _persistence.ExecuteQueryAsync<T>(sql, parms, ct);
    }

    public async Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileSelect<T>(
            _whereClauses, _orderBy, _orderByRaw, _joins,
            _groupByFields, _groupByRaw, _havingClauses,
            _skip, take: 1, _distinct, _persistence.Quote, _persistence.AppendLimitOffset);
        var rows = await _persistence.ExecuteQueryAsync<T>(sql, parms, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        var (sql, parms) = SelectQueryCompiler.CompileCount<T>(
            _whereClauses, _joins, _persistence.Quote);
        return _persistence.ExecuteScalarAsync<int>(sql, parms, ct);
    }

    public Task<bool> AnyAsync(CancellationToken ct = default)
        => AnyAsync(null, ct);

    public async Task<bool> AnyAsync(Expression<Func<T, bool>>? predicate, CancellationToken ct = default)
    {
        var clauses = new List<IConditionExpr>(_whereClauses);
        if (predicate is not null) clauses.Add(new LambdaConditionExpr(predicate));

        var (sql, parms) = SelectQueryCompiler.CompileCount<T>(clauses, _joins, _persistence.Quote);
        var count = await _persistence.ExecuteScalarAsync<int>(sql, parms, ct);
        return count > 0;
    }

    public async Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "SUM", selector, _whereClauses, _joins, _persistence.Quote);
        return await _persistence.ExecuteScalarAsync<TResult>(sql, parms, ct);
    }

    public async Task<TResult?> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "MAX", selector, _whereClauses, _joins, _persistence.Quote);
        return await _persistence.ExecuteScalarNullableAsync<TResult>(sql, parms, ct);
    }

    public async Task<TResult?> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "MIN", selector, _whereClauses, _joins, _persistence.Quote);
        return await _persistence.ExecuteScalarNullableAsync<TResult>(sql, parms, ct);
    }

    public async Task<double?> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        where TResult : struct
    {
        var (sql, parms) = SelectQueryCompiler.CompileAggregate<T>(
            "AVG", selector, _whereClauses, _joins, _persistence.Quote);
        return await _persistence.ExecuteScalarNullableAsync<double>(sql, parms, ct);
    }

    // ── Projection ────────────────────────────────────────────────────────

    public ISelectProjectedQuery<TDto> Select<TDto>() where TDto : class, new()
        => new SelectProjectedQuery<T, TDto>(_persistence, this);

    // ── Internal state exposure (for SelectProjectedQuery) ────────────────

    internal IDbPersistence                               Persistence    => _persistence;
    internal IReadOnlyList<IConditionExpr>                WhereClauses   => _whereClauses;
    internal IReadOnlyList<(PropertyInfo Prop, bool Desc)> OrderByList   => _orderBy;
    internal IReadOnlyList<string>                        OrderByRawList => _orderByRaw;
    internal IReadOnlyList<JoinClause>                    Joins          => _joins;
    internal IReadOnlyList<PropertyInfo>                  GroupByFields  => _groupByFields;
    internal IReadOnlyList<string>                        GroupByRawList => _groupByRaw;
    internal IReadOnlyList<IConditionExpr>                HavingClauses  => _havingClauses;
    internal int?                                         TakeRows       => _take;
    internal int?                                         SkipRows       => _skip;
    internal bool                                         DistinctFlag   => _distinct;

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PropertyInfo ExtractProp<TKey>(Expression<Func<T, TKey>> expr)
    {
        var body = expr.Body;
        if (body is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            body = u.Operand;
        if (body is MemberExpression m && m.Member is PropertyInfo pi)
            return pi;
        throw new ArgumentException(
            $"Key selector must be a simple property accessor (e.g. p => p.Name). Got: {expr.Body}",
            nameof(expr));
    }
}
