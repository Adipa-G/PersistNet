using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using PersistNet.Mapping;

namespace PersistNet.Query;

/// <summary>
/// Fluent expression builder entry point — mirrors the ndbgate <c>Expr.Build()</c> API
/// in idiomatic C#.
/// </summary>
/// <example>
/// <code>
/// // Field comparison
/// Expr.Field&lt;Product&gt;(p => p.Price).Gt().Value(100)
///
/// // Aggregate in HAVING
/// Expr.Count().Gt().Value(2)
/// Expr.Sum&lt;Order&gt;(o => o.Total).Ge().Value(500)
///
/// // Logical grouping
/// Expr.And(
///     Expr.Field&lt;Product&gt;(p => p.Price).Between().Values(50, 200),
///     Expr.Field&lt;Product&gt;(p => p.DeletedAt).IsNull())
/// </code>
/// </example>
public static class Expr
{
    // ── Field comparisons ─────────────────────────────────────────────────

    /// <summary>
    /// Begins a field-level expression for <typeparamref name="T"/>.
    /// The lambda must be a simple property accessor, e.g. <c>p => p.Price</c>.
    /// </summary>
    public static FieldExprBuilder<T> Field<T>(Expression<Func<T, object?>> field)
        => new(ExtractProperty(field));

    // ── Aggregate expressions (for HAVING clauses) ────────────────────────

    /// <summary><c>COUNT(*)</c> — use in HAVING: <c>Expr.Count().Gt().Value(2)</c>.</summary>
    public static AggregateExprBuilder Count()
        => new("COUNT", null, null);

    /// <summary><c>COUNT("col")</c> — use in HAVING.</summary>
    public static AggregateExprBuilder Count<T>(Expression<Func<T, object?>> field)
        => new("COUNT", ExtractProperty(field), typeof(T));

    /// <summary><c>SUM("col")</c> — use in HAVING: <c>Expr.Sum&lt;T&gt;(p => p.Amount).Gt().Value(1000)</c>.</summary>
    public static AggregateExprBuilder Sum<T>(Expression<Func<T, object?>> field)
        => new("SUM", ExtractProperty(field), typeof(T));

    /// <summary><c>AVG("col")</c> — use in HAVING.</summary>
    public static AggregateExprBuilder Avg<T>(Expression<Func<T, object?>> field)
        => new("AVG", ExtractProperty(field), typeof(T));

    /// <summary><c>MAX("col")</c> — use in HAVING.</summary>
    public static AggregateExprBuilder Max<T>(Expression<Func<T, object?>> field)
        => new("MAX", ExtractProperty(field), typeof(T));

    /// <summary><c>MIN("col")</c> — use in HAVING.</summary>
    public static AggregateExprBuilder Min<T>(Expression<Func<T, object?>> field)
        => new("MIN", ExtractProperty(field), typeof(T));

    // ── Logical combinators ───────────────────────────────────────────────

    /// <summary>Combines operands with SQL AND.</summary>
    public static IConditionExpr And(params IConditionExpr[] operands)
        => new LogicalExpr(isAnd: true, operands);

    /// <summary>Combines operands with SQL OR.</summary>
    public static IConditionExpr Or(params IConditionExpr[] operands)
        => new LogicalExpr(isAnd: false, operands);

    // ── Raw SQL escape hatch ──────────────────────────────────────────────

    /// <summary>
    /// Inserts a raw SQL fragment verbatim into a WHERE or HAVING clause.
    /// Parameters can be supplied as an anonymous object (<c>new { lo = 10, hi = 100 }</c>)
    /// or an <see cref="System.Collections.Generic.IDictionary{TKey,TValue}"/> of
    /// <c>string → object?</c>. Each key is bound as <c>@Key</c>.
    /// </summary>
    public static IConditionExpr RawSql(string sql, object? parameters = null)
    {
        var parms = DtoMapper.ExtractParameters(parameters);
        return new RawSqlConditionExpr(sql, parms);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static PropertyInfo ExtractProperty<T>(Expression<Func<T, object?>> expression)
    {
        var body = expression.Body;
        // Strip the boxing cast that C# adds for value types / when return type is object?
        if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            body = unary.Operand;
        if (body is MemberExpression member && member.Member is PropertyInfo pi)
            return pi;
        throw new ArgumentException(
            $"Expression must be a simple property accessor (e.g. p => p.MyProperty). Got: {expression.Body}",
            nameof(expression));
    }
}
