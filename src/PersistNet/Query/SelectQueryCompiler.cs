using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using PersistNet.DbInfo;

namespace PersistNet.Query;

/// <summary>
/// Holds mutable compilation state (parameter list + counter) shared across the
/// recursive SQL-generation walk.
/// </summary>
internal sealed class CompileContext
{
    internal List<(string Name, object? Value)> Parameters { get; } = [];
    private int _idx;

    internal string NextParam() => $"@p{_idx++}";

    /// <summary>
    /// Populated when the query has JOINs.
    /// Maps each entity type to its SQL table alias (e.g. <c>typeof(Order) → "t0"</c>).
    /// Empty in single-table queries (aliases are suppressed).
    /// </summary>
    internal Dictionary<Type, (Table Table, string Alias)> AliasMap { get; } = new();

    internal bool HasJoins => AliasMap.Count > 0;

    internal string? GetAlias(Type entityType)
        => AliasMap.TryGetValue(entityType, out var e) ? e.Alias : null;

    internal Table? GetTable(Type entityType)
        => AliasMap.TryGetValue(entityType, out var e) ? e.Table : null;
}

/// <summary>
/// Translates accumulated <see cref="ISelectQuery{T}"/> state into a parameterized
/// SQL string + parameter list suitable for <see cref="PersistNet.DbAbstraction.IDbPersistence"/>.
/// </summary>
internal static class SelectQueryCompiler
{
    // ── Projection column resolution record ──────────────────────────────

    /// <summary>Per-property resolution result used by <see cref="CompileSelectProjected{T,TDto}"/>.</summary>
    private sealed record DtoColResolution(
        PropertyInfo DtoProp,
        string?      Alias,       // SQL table alias ("t0", "t1", …) or null for single-table
        string       DbCol,       // DB column name to emit in SELECT / ORDER BY
        string       ReaderAlias  // AS alias → what DtoMapper matches on
    );

    // ── Public compile entry points ───────────────────────────────────────

    /// <summary>
    /// Compiles a SELECT … FROM … [JOIN …] WHERE … [GROUP BY …] [HAVING …] ORDER BY … query.
    /// LIMIT / OFFSET is appended via the provider-supplied <paramref name="appendLimitOffset"/> delegate.
    /// </summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileSelect<T>(
        IReadOnlyList<IConditionExpr>                whereClauses,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)> orderBy,
        IReadOnlyList<string>                         orderByRaw,
        IReadOnlyList<JoinClause>                     joins,
        IReadOnlyList<PropertyInfo>                   groupByFields,
        IReadOnlyList<string>                         groupByRaw,
        IReadOnlyList<IConditionExpr>                 havingClauses,
        int?                                          skip,
        int?                                          take,
        bool                                          distinct,
        Func<string, string>                          quote,
        Func<string, int?, int?, string>              appendLimitOffset)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = BuildContext(table, joins);

        // SELECT list — qualify with alias when joins are present
        var colList = string.Join(", ", table.Columns.Select(c =>
            ctx.HasJoins ? $"t0.{quote(c.ColumnName)}" : quote(c.ColumnName)));

        var distinctKeyword = distinct ? "DISTINCT " : "";

        // FROM clause
        var fromClause = ctx.HasJoins
            ? $"{QualifyTable(table, quote)} t0"
            : QualifyTable(table, quote);

        var sb = new StringBuilder($"SELECT {distinctKeyword}{colList} FROM {fromClause}");

        // JOIN clauses
        AppendJoins(sb, joins, quote, ctx);

        AppendWhere(sb, whereClauses, table, quote, ctx);
        AppendGroupBy(sb, groupByFields, groupByRaw, table, quote, ctx);
        AppendHaving(sb, havingClauses, table, quote, ctx);
        AppendOrderBy(sb, orderBy, orderByRaw, table, quote, ctx);

        var sql = appendLimitOffset(sb.ToString(), skip, take);
        return (sql, ctx.Parameters);
    }

    /// <summary>Compiles SELECT COUNT(*) FROM … [JOIN …] WHERE …</summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileCount<T>(
        IReadOnlyList<IConditionExpr> whereClauses,
        IReadOnlyList<JoinClause>     joins,
        Func<string, string>          quote)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = BuildContext(table, joins);

        var fromClause = ctx.HasJoins
            ? $"{QualifyTable(table, quote)} t0"
            : QualifyTable(table, quote);

        var sb = new StringBuilder($"SELECT COUNT(*) FROM {fromClause}");
        AppendJoins(sb, joins, quote, ctx);
        AppendWhere(sb, whereClauses, table, quote, ctx);

        return (sb.ToString(), ctx.Parameters);
    }

    /// <summary>Compiles SELECT {aggFunc}("col") FROM … [JOIN …] WHERE …</summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileAggregate<T>(
        string                        aggFunc,
        LambdaExpression              selector,
        IReadOnlyList<IConditionExpr> whereClauses,
        IReadOnlyList<JoinClause>     joins,
        Func<string, string>          quote)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = BuildContext(table, joins);

        var colRef = ResolveColumn(selector, table, quote, ctx);
        var fromClause = ctx.HasJoins
            ? $"{QualifyTable(table, quote)} t0"
            : QualifyTable(table, quote);

        var sb = new StringBuilder($"SELECT {aggFunc}({colRef}) FROM {fromClause}");
        AppendJoins(sb, joins, quote, ctx);
        AppendWhere(sb, whereClauses, table, quote, ctx);

        return (sb.ToString(), ctx.Parameters);
    }

    // ── Alias context builder ─────────────────────────────────────────────

    /// <summary>
    /// Compiles a projection query: SELECT [DTO columns] FROM … [JOIN …] WHERE … ORDER BY …
    /// The SELECT list is derived from <typeparamref name="TDto"/>'s <see cref="ColumnInfo"/>
    /// properties. Each column is emitted with an AS alias so that
    /// <see cref="PersistNet.Mapping.DtoMapper"/> can match reader fields back to
    /// DTO properties by name. Use <see cref="FromTableAttribute"/> on DTO properties to
    /// disambiguate when the same column name exists in multiple joined tables.
    /// </summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileSelectProjected<T, TDto>(
        IReadOnlyList<IConditionExpr>                  whereClauses,
        IReadOnlyList<JoinClause>                      joins,
        IReadOnlyList<PropertyInfo>                    groupByFields,
        IReadOnlyList<string>                          groupByRaw,
        IReadOnlyList<IConditionExpr>                  havingClauses,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)>  entityOrderBy,
        IReadOnlyList<string>                          entityOrderByRaw,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)>  dtoOrderBy,
        IReadOnlyList<string>                          dtoOrderByRaw,
        int?                                           skip,
        int?                                           take,
        bool                                           distinct,
        Func<string, string>                           quote,
        Func<string, int?, int?, string>               appendLimitOffset)
        where T : class
        where TDto : class, new()
    {
        var primaryTable = RequireTable<T>();
        var ctx          = BuildContext(primaryTable, joins);

        var colResolutions = BuildDtoColumnResolutions<TDto>(primaryTable, joins, ctx);

        var distinctKeyword = distinct ? "DISTINCT " : "";
        var colList = string.Join(", ", colResolutions.Select(r =>
        {
            var colRef = r.Alias is not null
                ? $"{r.Alias}.{quote(r.DbCol)}"
                : quote(r.DbCol);
            return $"{colRef} AS {quote(r.ReaderAlias)}";
        }));

        var fromClause = ctx.HasJoins
            ? $"{QualifyTable(primaryTable, quote)} t0"
            : QualifyTable(primaryTable, quote);

        var sb = new StringBuilder($"SELECT {distinctKeyword}{colList} FROM {fromClause}");

        AppendJoins(sb, joins, quote, ctx);
        AppendWhere(sb, whereClauses, primaryTable, quote, ctx);
        AppendGroupBy(sb, groupByFields, groupByRaw, primaryTable, quote, ctx);
        AppendHaving(sb, havingClauses, primaryTable, quote, ctx);
        AppendProjectedOrderBy(sb, entityOrderBy, entityOrderByRaw, dtoOrderBy, dtoOrderByRaw,
            primaryTable, colResolutions, quote, ctx);

        var sql = appendLimitOffset(sb.ToString(), skip, take);
        return (sql, ctx.Parameters);
    }

    // ── Projected ORDER BY ────────────────────────────────────────────────

    private static void AppendProjectedOrderBy(
        StringBuilder                                  sb,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)>  entityOrderBy,
        IReadOnlyList<string>                          entityOrderByRaw,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)>  dtoOrderBy,
        IReadOnlyList<string>                          dtoOrderByRaw,
        Table                                          primaryTable,
        IReadOnlyList<DtoColResolution>                colResolutions,
        Func<string, string>                           quote,
        CompileContext                                  ctx)
    {
        var parts = new List<string>();

        // Entity-property ordering from before Select<TDto>() — resolved against source tables.
        foreach (var o in entityOrderBy)
        {
            var col = primaryTable.Columns.FirstOrDefault(c => c.Property == o.Prop)
                ?? throw new InvalidOperationException(
                    $"Property '{o.Prop.Name}' was not found in table '{primaryTable.Name}'.");
            var colRef = ctx.HasJoins ? $"t0.{quote(col.ColumnName)}" : quote(col.ColumnName);
            parts.Add($"{colRef} {(o.Desc ? "DESC" : "ASC")}");
        }

        parts.AddRange(entityOrderByRaw);

        // DTO-property ordering from after Select<TDto>() — resolved via projection map.
        foreach (var o in dtoOrderBy)
        {
            var res = colResolutions.FirstOrDefault(r => r.DtoProp == o.Prop)
                ?? throw new InvalidOperationException(
                    $"DTO property '{o.Prop.Name}' used in OrderBy was not found in the projection. " +
                    "Ensure it has a [ColumnInfo] attribute.");
            var colRef = res.Alias is not null
                ? $"{res.Alias}.{quote(res.DbCol)}"
                : quote(res.DbCol);
            parts.Add($"{colRef} {(o.Desc ? "DESC" : "ASC")}");
        }

        parts.AddRange(dtoOrderByRaw);

        if (parts.Count > 0)
            sb.Append(" ORDER BY ").Append(string.Join(", ", parts));
    }

    // ── DTO column resolution ─────────────────────────────────────────────

    private static IReadOnlyList<DtoColResolution> BuildDtoColumnResolutions<TDto>(
        Table                     primaryTable,
        IReadOnlyList<JoinClause> joins,
        CompileContext             ctx)
        where TDto : class, new()
    {
        var results = new List<DtoColResolution>();

        foreach (var dtoProp in typeof(TDto).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var colAttr = dtoProp.GetCustomAttribute<ColumnInfo>();
            if (colAttr is null) continue;

            var fromAttr    = dtoProp.GetCustomAttribute<FromTableAttribute>();
            var readerAlias = colAttr.ColumnName ?? dtoProp.Name;
            var dbColLookup = fromAttr?.ColumnName ?? readerAlias;

            string? resolvedAlias;

            if (fromAttr is not null)
            {
                // Explicit table specified via [FromTable].
                var targetType = fromAttr.TableType;
                if (targetType == primaryTable.EntityType)
                {
                    if (!primaryTable.Columns.Any(c => c.ColumnName == dbColLookup))
                        throw new InvalidOperationException(
                            $"[FromTable(typeof({targetType.Name}))] on DTO property '{dtoProp.Name}': " +
                            $"column '{dbColLookup}' was not found in table '{primaryTable.Name}'.");
                    resolvedAlias = ctx.HasJoins ? "t0" : null;
                }
                else if (ctx.AliasMap.TryGetValue(targetType, out var entry))
                {
                    if (!entry.Table.Columns.Any(c => c.ColumnName == dbColLookup))
                        throw new InvalidOperationException(
                            $"[FromTable(typeof({targetType.Name}))] on DTO property '{dtoProp.Name}': " +
                            $"column '{dbColLookup}' was not found in table '{entry.Table.Name}'.");
                    resolvedAlias = entry.Alias;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[FromTable(typeof({targetType.Name}))] on DTO property '{dtoProp.Name}' refers to " +
                        $"a type that is not part of this query. Add it via InnerJoin or LeftJoin, " +
                        $"or use it as the primary entity type.");
                }
            }
            else
            {
                // Auto-resolve: search primary table first, then joined tables in order.
                bool found = false;
                resolvedAlias = null;

                if (primaryTable.Columns.Any(c => c.ColumnName == dbColLookup))
                {
                    resolvedAlias = ctx.HasJoins ? "t0" : null;
                    found = true;
                }
                else
                {
                    foreach (var j in joins)
                    {
                        if (j.JoinedTable.Columns.Any(c => c.ColumnName == dbColLookup))
                        {
                            resolvedAlias = ctx.GetAlias(j.JoinedType)!;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                    throw new InvalidOperationException(
                        $"Column '{dbColLookup}' not found in any queried table for DTO property " +
                        $"'{dtoProp.Name}'. Ensure the column name matches a [ColumnInfo] column on " +
                        $"the queried entity type(s), or use [FromTable] to specify the source table.");
            }

            results.Add(new DtoColResolution(dtoProp, resolvedAlias, dbColLookup, readerAlias));
        }

        if (results.Count == 0)
            throw new InvalidOperationException(
                $"DTO type '{typeof(TDto).Name}' has no properties decorated with [ColumnInfo]. " +
                "Projection requires at least one [ColumnInfo]-decorated property.");

        return results;
    }

    /// <summary>
    /// Builds the alias map from the primary table and any JOINs.
    /// When there are no joins the map is empty and all column refs are unqualified.
    /// </summary>
    private static CompileContext BuildContext(Table primaryTable, IReadOnlyList<JoinClause> joins)
    {
        var ctx = new CompileContext();
        if (joins.Count == 0) return ctx;

        ctx.AliasMap[primaryTable.EntityType] = (primaryTable, "t0");
        var i = 1;
        foreach (var j in joins)
            ctx.AliasMap[j.JoinedType] = (j.JoinedTable, $"t{i++}");

        return ctx;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static Table RequireTable<T>() where T : class
        => DbInfoCache.FindTable(typeof(T))
           ?? throw new InvalidOperationException(
               $"Type '{typeof(T).Name}' is not registered as a table entity. " +
               "Ensure it has a [TableInfo] attribute.");

    private static string QualifyTable(Table table, Func<string, string> quote)
        => table.Schema is not null
            ? $"{quote(table.Schema)}.{quote(table.Name)}"
            : quote(table.Name);

    // ── JOIN emission ─────────────────────────────────────────────────────

    private static void AppendJoins(
        StringBuilder            sb,
        IReadOnlyList<JoinClause> joins,
        Func<string, string>      quote,
        CompileContext             ctx)
    {
        foreach (var j in joins)
        {
            var alias      = ctx.GetAlias(j.JoinedType)!;
            var joinTable  = $"{QualifyTable(j.JoinedTable, quote)} {alias}";
            var condSql    = new PredicateVisitor(ctx, quote).Visit(j.Condition.Body);
            sb.Append($" {j.JoinKeyword} {joinTable} ON ({condSql})");
        }
    }

    // ── WHERE / HAVING emission ───────────────────────────────────────────

    private static void AppendWhere(
        StringBuilder                 sb,
        IReadOnlyList<IConditionExpr> whereClauses,
        Table                         primaryTable,
        Func<string, string>          quote,
        CompileContext                 ctx)
    {
        if (whereClauses.Count == 0) return;

        var conditions = whereClauses
            .Select(w => EmitCondition(w, primaryTable, quote, ctx))
            .ToList();

        sb.Append(" WHERE ");
        sb.Append(conditions.Count == 1
            ? conditions[0]
            : string.Join(" AND ", conditions.Select(c => $"({c})")));
    }

    private static void AppendHaving(
        StringBuilder                 sb,
        IReadOnlyList<IConditionExpr> havingClauses,
        Table                         primaryTable,
        Func<string, string>          quote,
        CompileContext                 ctx)
    {
        if (havingClauses.Count == 0) return;

        var conditions = havingClauses
            .Select(h => EmitCondition(h, primaryTable, quote, ctx))
            .ToList();

        sb.Append(" HAVING ");
        sb.Append(conditions.Count == 1
            ? conditions[0]
            : string.Join(" AND ", conditions.Select(c => $"({c})")));
    }

    // ── GROUP BY emission ─────────────────────────────────────────────────

    private static void AppendGroupBy(
        StringBuilder              sb,
        IReadOnlyList<PropertyInfo> groupByFields,
        IReadOnlyList<string>       groupByRaw,
        Table                       table,
        Func<string, string>        quote,
        CompileContext               ctx)
    {
        var parts = new List<string>();

        foreach (var prop in groupByFields)
        {
            var col = table.Columns.FirstOrDefault(c => c.Property == prop)
                ?? throw new InvalidOperationException(
                    $"Property '{prop.Name}' was not found in table '{table.Name}'.");
            parts.Add(ctx.HasJoins ? $"t0.{quote(col.ColumnName)}" : quote(col.ColumnName));
        }

        parts.AddRange(groupByRaw);

        if (parts.Count > 0)
            sb.Append(" GROUP BY ").Append(string.Join(", ", parts));
    }

    // ── ORDER BY emission ─────────────────────────────────────────────────

    private static void AppendOrderBy(
        StringBuilder                              sb,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)> orderBy,
        IReadOnlyList<string>                       orderByRaw,
        Table                                       table,
        Func<string, string>                        quote,
        CompileContext                               ctx)
    {
        var parts = new List<string>();

        foreach (var o in orderBy)
        {
            var col = table.Columns.FirstOrDefault(c => c.Property == o.Prop)
                ?? throw new InvalidOperationException(
                    $"Property '{o.Prop.Name}' was not found in table '{table.Name}'.");
            var colRef = ctx.HasJoins ? $"t0.{quote(col.ColumnName)}" : quote(col.ColumnName);
            parts.Add($"{colRef} {(o.Desc ? "DESC" : "ASC")}");
        }

        parts.AddRange(orderByRaw);

        if (parts.Count > 0)
            sb.Append(" ORDER BY ").Append(string.Join(", ", parts));
    }

    // ── Condition emission ────────────────────────────────────────────────

    private static string EmitCondition(
        IConditionExpr       expr,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        return expr switch
        {
            LambdaConditionExpr    lc => EmitLambda(lc, primaryTable, quote, ctx),
            ComparisonExpr         ce => EmitComparison(ce, primaryTable, quote, ctx),
            LogicalExpr            le => EmitLogical(le, primaryTable, quote, ctx),
            NullCheckExpr          nc => EmitNullCheck(nc, primaryTable, quote, ctx),
            RawSqlConditionExpr    rs => EmitRawSql(rs, ctx),
            AggregateConditionExpr ac => EmitAggregateCondition(ac, primaryTable, quote, ctx),
            _ => throw new NotSupportedException($"Unknown condition expression type: {expr.GetType().Name}")
        };
    }

    private static string EmitLambda(
        LambdaConditionExpr  lc,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        // If the lambda has an EntityType override (from Where<TJoin>), find that table;
        // otherwise use the primary table.
        Table resolvedTable = primaryTable;
        if (lc.EntityType is not null && ctx.AliasMap.TryGetValue(lc.EntityType, out var entry))
            resolvedTable = entry.Table;

        return new PredicateVisitor(ctx, quote, resolvedTable).Visit(lc.Lambda.Body);
    }

    private static string EmitComparison(
        ComparisonExpr       ce,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var (colName, qualifiedCol) = ResolveEntityColumn(ce.Property, ce.EntityType, primaryTable, quote, ctx);

        return ce.Op switch
        {
            ComparisonOp.Eq      => $"{qualifiedCol} = {Param(ctx, ce.Values[0])}",
            ComparisonOp.Neq     => $"{qualifiedCol} != {Param(ctx, ce.Values[0])}",
            ComparisonOp.Gt      => $"{qualifiedCol} > {Param(ctx, ce.Values[0])}",
            ComparisonOp.Ge      => $"{qualifiedCol} >= {Param(ctx, ce.Values[0])}",
            ComparisonOp.Lt      => $"{qualifiedCol} < {Param(ctx, ce.Values[0])}",
            ComparisonOp.Le      => $"{qualifiedCol} <= {Param(ctx, ce.Values[0])}",
            ComparisonOp.Like    => $"{qualifiedCol} LIKE {Param(ctx, ce.Values[0])}",
            ComparisonOp.Between =>
                $"{qualifiedCol} BETWEEN {Param(ctx, ce.Values[0])} AND {Param(ctx, ce.Values[1])}",
            ComparisonOp.In      => EmitIn(qualifiedCol, ce.Values, ctx),
            _ => throw new ArgumentOutOfRangeException(nameof(ce.Op), $"Unknown op: {ce.Op}")
        };
    }

    private static string EmitIn(string qualifiedCol, object?[] values, CompileContext ctx)
    {
        if (values.Length == 0) return "1=0"; // empty IN → always false
        var names = values.Select(v => Param(ctx, v));
        return $"{qualifiedCol} IN ({string.Join(", ", names)})";
    }

    private static string EmitLogical(
        LogicalExpr          le,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var keyword = le.IsAnd ? "AND" : "OR";
        var parts   = le.Operands.Select(o => $"({EmitCondition(o, primaryTable, quote, ctx)})");
        return string.Join($" {keyword} ", parts);
    }

    private static string EmitNullCheck(
        NullCheckExpr        nc,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var (_, qualifiedCol) = ResolveEntityColumn(nc.Property, nc.EntityType, primaryTable, quote, ctx);
        return nc.IsNull ? $"{qualifiedCol} IS NULL" : $"{qualifiedCol} IS NOT NULL";
    }

    private static string EmitRawSql(RawSqlConditionExpr rs, CompileContext ctx)
    {
        // Register any bound parameters from the raw SQL expression
        ctx.Parameters.AddRange(rs.Params);
        return rs.Sql;
    }

    private static string EmitAggregateCondition(
        AggregateConditionExpr ac,
        Table                  primaryTable,
        Func<string, string>   quote,
        CompileContext          ctx)
    {
        string aggArg;
        if (ac.Property is null)
        {
            aggArg = "*";
        }
        else
        {
            var entityType = ac.EntityType ?? primaryTable.EntityType;
            var (_, colRef) = ResolveEntityColumn(ac.Property, entityType, primaryTable, quote, ctx);
            aggArg = colRef;
        }

        var aggSql = $"{ac.AggFunc}({aggArg})";
        var opSql  = ac.Op switch
        {
            ComparisonOp.Eq  => "=",
            ComparisonOp.Neq => "!=",
            ComparisonOp.Gt  => ">",
            ComparisonOp.Ge  => ">=",
            ComparisonOp.Lt  => "<",
            ComparisonOp.Le  => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(ac.Op), $"Unsupported aggregate op: {ac.Op}")
        };

        return $"{aggSql} {opSql} {Param(ctx, ac.Values[0])}";
    }

    // ── Column resolution helpers ─────────────────────────────────────────

    /// <summary>
    /// Resolves a column reference for an expression that carries an entity type
    /// (e.g. <see cref="ComparisonExpr"/> or <see cref="NullCheckExpr"/>).
    /// Returns the bare column name and the SQL-ready qualified column reference.
    /// </summary>
    private static (string ColName, string QualifiedCol) ResolveEntityColumn(
        PropertyInfo         prop,
        Type                 entityType,
        Table                primaryTable,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        Table table;
        string? alias;

        if (ctx.HasJoins && ctx.AliasMap.TryGetValue(entityType, out var entry))
        {
            table = entry.Table;
            alias = entry.Alias;
        }
        else
        {
            table = primaryTable;
            alias = ctx.HasJoins ? "t0" : null;
        }

        var col = table.Columns.FirstOrDefault(c => c.Property == prop)
            ?? throw new InvalidOperationException(
                $"Property '{prop.Name}' was not found in table '{table.Name}'. " +
                "Ensure it has a [ColumnInfo] attribute.");

        var qualifiedCol = alias is not null
            ? $"{alias}.{quote(col.ColumnName)}"
            : quote(col.ColumnName);

        return (col.ColumnName, qualifiedCol);
    }

    private static string FindColumn(PropertyInfo prop, Table table)
    {
        var col = table.Columns.FirstOrDefault(c => c.Property == prop)
            ?? throw new InvalidOperationException(
                $"Property '{prop.Name}' was not found in table '{table.Name}'. " +
                "Ensure it has a [ColumnInfo] attribute.");
        return col.ColumnName;
    }

    private static string ResolveColumn(
        LambdaExpression     selector,
        Table                table,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var body = selector.Body;
        if (body is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            body = u.Operand;
        if (body is MemberExpression m && m.Member is PropertyInfo pi)
        {
            var col = table.Columns.FirstOrDefault(c => c.Property == pi)
                ?? throw new InvalidOperationException(
                    $"Property '{pi.Name}' was not found in table '{table.Name}'.");
            return ctx.HasJoins ? $"t0.{quote(col.ColumnName)}" : quote(col.ColumnName);
        }
        throw new ArgumentException(
            $"Aggregate selector must be a simple property accessor. Got: {selector.Body}");
    }

    // ── Parameter helper ─────────────────────────────────────────────────

    private static string Param(CompileContext ctx, object? value)
    {
        var name = ctx.NextParam();
        ctx.Parameters.Add((name, value));
        return name;
    }
}
