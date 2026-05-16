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
}

/// <summary>
/// Translates accumulated <see cref="ISelectQuery{T}"/> state into a parameterized
/// SQL string + parameter list suitable for <see cref="PersistNet.DbAbstraction.IDbPersistence"/>.
/// </summary>
internal static class SelectQueryCompiler
{
    // ── Public compile entry points ───────────────────────────────────────

    /// <summary>
    /// Compiles a SELECT … FROM … WHERE … ORDER BY … query.
    /// LIMIT / OFFSET is appended via the provider-supplied <paramref name="appendLimitOffset"/> delegate.
    /// </summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileSelect<T>(
        IReadOnlyList<IConditionExpr>           whereClauses,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)> orderBy,
        int?                                    skip,
        int?                                    take,
        Func<string, string>                    quote,
        Func<string, int?, int?, string>        appendLimitOffset)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = new CompileContext();

        var colList   = string.Join(", ", table.Columns.Select(c => quote(c.ColumnName)));
        var tableName = QualifyTable(table, quote);

        var sb = new StringBuilder($"SELECT {colList} FROM {tableName}");

        AppendWhere(sb, whereClauses, table, quote, ctx);
        AppendOrderBy(sb, orderBy, table, quote);

        var sql = appendLimitOffset(sb.ToString(), skip, take);
        return (sql, ctx.Parameters);
    }

    /// <summary>Compiles SELECT COUNT(*) FROM … WHERE …</summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileCount<T>(
        IReadOnlyList<IConditionExpr> whereClauses,
        Func<string, string>          quote)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = new CompileContext();

        var sb = new StringBuilder($"SELECT COUNT(*) FROM {QualifyTable(table, quote)}");
        AppendWhere(sb, whereClauses, table, quote, ctx);

        return (sb.ToString(), ctx.Parameters);
    }

    /// <summary>Compiles SELECT {aggFunc}("col") FROM … WHERE …</summary>
    internal static (string Sql, List<(string, object?)> Parameters) CompileAggregate<T>(
        string                        aggFunc,
        LambdaExpression              selector,
        IReadOnlyList<IConditionExpr> whereClauses,
        Func<string, string>          quote)
        where T : class
    {
        var table = RequireTable<T>();
        var ctx   = new CompileContext();

        var colName = ResolveColumn(selector, table, quote);
        var sb      = new StringBuilder($"SELECT {aggFunc}({colName}) FROM {QualifyTable(table, quote)}");
        AppendWhere(sb, whereClauses, table, quote, ctx);

        return (sb.ToString(), ctx.Parameters);
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

    private static void AppendWhere(
        StringBuilder                 sb,
        IReadOnlyList<IConditionExpr> whereClauses,
        Table                         table,
        Func<string, string>          quote,
        CompileContext                ctx)
    {
        if (whereClauses.Count == 0) return;

        var conditions = whereClauses
            .Select(w => EmitCondition(w, table, quote, ctx))
            .ToList();

        // Multiple top-level .Where() calls → ANDed, each wrapped in parens
        sb.Append(" WHERE ");
        if (conditions.Count == 1)
        {
            sb.Append(conditions[0]);
        }
        else
        {
            sb.Append(string.Join(" AND ", conditions.Select(c => $"({c})")));
        }
    }

    private static void AppendOrderBy(
        StringBuilder                             sb,
        IReadOnlyList<(PropertyInfo Prop, bool Desc)> orderBy,
        Table                                     table,
        Func<string, string>                      quote)
    {
        if (orderBy.Count == 0) return;

        var parts = orderBy.Select(o =>
        {
            var col = table.Columns.FirstOrDefault(c => c.Property == o.Prop)
                ?? throw new InvalidOperationException(
                    $"Property '{o.Prop.Name}' was not found in table '{table.Name}'.");
            return $"{quote(col.ColumnName)} {(o.Desc ? "DESC" : "ASC")}";
        });

        sb.Append(" ORDER BY ").Append(string.Join(", ", parts));
    }

    // ── Condition emission ────────────────────────────────────────────────

    private static string EmitCondition(
        IConditionExpr       expr,
        Table                table,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        return expr switch
        {
            LambdaConditionExpr lc => new PredicateVisitor(table, quote, ctx).Visit(lc.Lambda.Body),
            ComparisonExpr      ce => EmitComparison(ce, table, quote, ctx),
            LogicalExpr         le => EmitLogical(le, table, quote, ctx),
            NullCheckExpr       nc => EmitNullCheck(nc, table, quote),
            _ => throw new NotSupportedException($"Unknown condition expression type: {expr.GetType().Name}")
        };
    }

    private static string EmitComparison(
        ComparisonExpr       ce,
        Table                table,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var col = FindColumn(ce.Property, table);

        return ce.Op switch
        {
            ComparisonOp.Eq      => $"{quote(col)} = {Param(ctx, ce.Values[0])}",
            ComparisonOp.Neq     => $"{quote(col)} != {Param(ctx, ce.Values[0])}",
            ComparisonOp.Gt      => $"{quote(col)} > {Param(ctx, ce.Values[0])}",
            ComparisonOp.Ge      => $"{quote(col)} >= {Param(ctx, ce.Values[0])}",
            ComparisonOp.Lt      => $"{quote(col)} < {Param(ctx, ce.Values[0])}",
            ComparisonOp.Le      => $"{quote(col)} <= {Param(ctx, ce.Values[0])}",
            ComparisonOp.Like    => $"{quote(col)} LIKE {Param(ctx, ce.Values[0])}",
            ComparisonOp.Between =>
                $"{quote(col)} BETWEEN {Param(ctx, ce.Values[0])} AND {Param(ctx, ce.Values[1])}",
            ComparisonOp.In      => EmitIn(quote(col), ce.Values, ctx),
            _ => throw new ArgumentOutOfRangeException(nameof(ce.Op), $"Unknown op: {ce.Op}")
        };
    }

    private static string EmitIn(string quotedCol, object?[] values, CompileContext ctx)
    {
        if (values.Length == 0) return "1=0"; // empty IN → always false

        var names = values.Select(v => Param(ctx, v));
        return $"{quotedCol} IN ({string.Join(", ", names)})";
    }

    private static string EmitLogical(
        LogicalExpr          le,
        Table                table,
        Func<string, string> quote,
        CompileContext        ctx)
    {
        var keyword = le.IsAnd ? "AND" : "OR";
        var parts   = le.Operands.Select(o => $"({EmitCondition(o, table, quote, ctx)})");
        return string.Join($" {keyword} ", parts);
    }

    private static string EmitNullCheck(
        NullCheckExpr        nc,
        Table                table,
        Func<string, string> quote)
    {
        var col = FindColumn(nc.Property, table);
        return nc.IsNull ? $"{quote(col)} IS NULL" : $"{quote(col)} IS NOT NULL";
    }

    // ── Column resolution helpers ─────────────────────────────────────────

    private static string FindColumn(PropertyInfo prop, Table table)
    {
        var col = table.Columns.FirstOrDefault(c => c.Property == prop)
            ?? throw new InvalidOperationException(
                $"Property '{prop.Name}' was not found in table '{table.Name}'. " +
                "Ensure it has a [ColumnInfo] attribute.");
        return col.ColumnName;
    }

    private static string ResolveColumn(LambdaExpression selector, Table table, Func<string, string> quote)
    {
        var body = selector.Body;
        if (body is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            body = u.Operand;
        if (body is MemberExpression m && m.Member is PropertyInfo pi)
        {
            var col = table.Columns.FirstOrDefault(c => c.Property == pi)
                ?? throw new InvalidOperationException(
                    $"Property '{pi.Name}' was not found in table '{table.Name}'.");
            return quote(col.ColumnName);
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
