using System;
using System.Reflection;

namespace PersistNet.Query;

/// <summary>
/// Fluent builder that captures the pending operator and accepts a value (or values)
/// to complete the expression.
/// </summary>
public sealed class ValueExprBuilder<T>
{
    private readonly PropertyInfo _property;
    private readonly ComparisonOp _op;

    internal ValueExprBuilder(PropertyInfo property, ComparisonOp op)
    {
        _property = property;
        _op       = op;
    }

    /// <summary>
    /// Completes a single-value comparison: Eq, Neq, Gt, Ge, Lt, Le, Like.
    /// </summary>
    public IConditionExpr Value(object? value)
        => new ComparisonExpr(_property, typeof(T), _op, [value]);

    /// <summary>
    /// Completes a multi-value comparison: <c>Between(lo, hi)</c> or
    /// <c>In(v1, v2, v3, ...)</c>.
    /// </summary>
    public IConditionExpr Values(params object?[] values)
        => new ComparisonExpr(_property, typeof(T), _op, values);
}
