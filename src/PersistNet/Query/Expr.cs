using System;
using System.Linq.Expressions;
using System.Reflection;

namespace PersistNet.Query;

/// <summary>
/// Fluent expression builder entry point — mirrors the ndbgate <c>Expr.Build()</c> API
/// in idiomatic C#.
/// </summary>
/// <example>
/// <code>
/// // Single comparison
/// Expr.Field&lt;Product&gt;(p => p.Price).Gt().Value(100)
///
/// // Logical grouping
/// Expr.And(
///     Expr.Field&lt;Product&gt;(p => p.Price).Between().Values(50, 200),
///     Expr.Field&lt;Product&gt;(p => p.DeletedAt).IsNull())
/// </code>
/// </example>
public static class Expr
{
    /// <summary>
    /// Begins a field-level expression for <typeparamref name="T"/>.
    /// The lambda must be a simple property accessor, e.g. <c>p => p.Price</c>.
    /// </summary>
    public static FieldExprBuilder<T> Field<T>(Expression<Func<T, object?>> field)
    {
        var prop = ExtractProperty(field);
        return new FieldExprBuilder<T>(prop);
    }

    /// <summary>Combines operands with SQL AND.</summary>
    public static IConditionExpr And(params IConditionExpr[] operands)
        => new LogicalExpr(isAnd: true, operands);

    /// <summary>Combines operands with SQL OR.</summary>
    public static IConditionExpr Or(params IConditionExpr[] operands)
        => new LogicalExpr(isAnd: false, operands);

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
