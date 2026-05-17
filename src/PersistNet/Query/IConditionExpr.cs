using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace PersistNet.Query;

/// <summary>
/// Marker interface for all condition expressions that can be passed to
/// <see cref="ISelectQuery{T}.Where(IConditionExpr)"/>.
/// </summary>
public interface IConditionExpr { }

// ── Internal concrete implementations ────────────────────────────────────────

/// <summary>
/// Wraps a C# lambda predicate such as <c>p => p.Price > 100</c>.
/// The <see cref="PredicateVisitor"/> translates it to parameterized SQL at
/// query-compile time.
/// </summary>
internal sealed class LambdaConditionExpr : IConditionExpr
{
    internal LambdaExpression Lambda     { get; }
    /// <summary>
    /// Non-null when this predicate applies to a <em>joined</em> entity type
    /// (e.g. from <c>.Where&lt;TJoin&gt;(j => j.Country == "AU")</c>).
    /// <c>null</c> means it applies to the primary entity.
    /// </summary>
    internal System.Type?     EntityType { get; }

    internal LambdaConditionExpr(LambdaExpression lambda, System.Type? entityType = null)
    {
        Lambda     = lambda;
        EntityType = entityType;
    }
}

/// <summary>
/// A single field-comparison built with the ndbgate-style expression builder:
/// <c>Expr.Field&lt;T&gt;(f => f.Price).Gt().Value(100)</c>.
/// </summary>
internal sealed class ComparisonExpr : IConditionExpr
{
    internal PropertyInfo Property   { get; }
    internal System.Type  EntityType { get; }
    internal ComparisonOp Op         { get; }
    /// <summary>
    /// Single value for Eq/Neq/Gt/Ge/Lt/Le/Like; two values (lo, hi) for Between;
    /// arbitrary count for In.
    /// </summary>
    internal object?[]    Values     { get; }

    internal ComparisonExpr(PropertyInfo property, System.Type entityType, ComparisonOp op, object?[] values)
    {
        Property   = property;
        EntityType = entityType;
        Op         = op;
        Values     = values;
    }
}

/// <summary>
/// Combines two or more conditions with AND or OR:
/// <c>Expr.And(expr1, expr2)</c> / <c>Expr.Or(expr1, expr2)</c>.
/// </summary>
internal sealed class LogicalExpr : IConditionExpr
{
    internal bool            IsAnd    { get; }
    internal IConditionExpr[] Operands { get; }

    internal LogicalExpr(bool isAnd, IConditionExpr[] operands)
    {
        IsAnd    = isAnd;
        Operands = operands;
    }
}

/// <summary>
/// Null / not-null check: <c>Expr.Field&lt;T&gt;(f => f.DeletedAt).IsNull()</c>.
/// </summary>
internal sealed class NullCheckExpr : IConditionExpr
{
    internal PropertyInfo Property   { get; }
    internal System.Type  EntityType { get; }
    internal bool         IsNull     { get; }

    internal NullCheckExpr(PropertyInfo property, System.Type entityType, bool isNull)
    {
        Property   = property;
        EntityType = entityType;
        IsNull     = isNull;
    }
}

/// <summary>
/// A raw SQL fragment inserted verbatim into a WHERE or HAVING clause, with optional
/// bound parameters supplied as an anonymous object or dictionary.
/// Example: <c>.Where("Price BETWEEN @lo AND @hi", new { lo = 10, hi = 100 })</c>
/// </summary>
internal sealed class RawSqlConditionExpr : IConditionExpr
{
    internal string                              Sql    { get; }
    internal List<(string Name, object? Value)>  Params { get; }

    internal RawSqlConditionExpr(string sql, List<(string Name, object? Value)> @params)
    {
        Sql    = sql;
        Params = @params;
    }
}

/// <summary>
/// An aggregate comparison for use in HAVING clauses:
/// <c>Expr.Count().Gt().Value(2)</c> → <c>COUNT(*) &gt; @p0</c>.
/// <c>Expr.Sum&lt;T&gt;(p => p.Amount).Gt().Value(1000)</c> → <c>SUM("Amount") &gt; @p0</c>.
/// </summary>
internal sealed class AggregateConditionExpr : IConditionExpr
{
    internal string        AggFunc    { get; }  // "COUNT", "SUM", "AVG", "MAX", "MIN"
    internal PropertyInfo? Property   { get; }  // null → COUNT(*)
    internal System.Type?  EntityType { get; }
    internal ComparisonOp  Op         { get; }
    internal object?[]     Values     { get; }

    internal AggregateConditionExpr(
        string        aggFunc,
        PropertyInfo? property,
        System.Type?  entityType,
        ComparisonOp  op,
        object?[]     values)
    {
        AggFunc    = aggFunc;
        Property   = property;
        EntityType = entityType;
        Op         = op;
        Values     = values;
    }
}
