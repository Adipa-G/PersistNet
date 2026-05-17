using System;
using System.Reflection;

namespace PersistNet.Query;

/// <summary>
/// Fluent builder for aggregate expressions used in <c>.Having()</c> clauses.
/// Returned by <see cref="Expr.Count()"/>, <see cref="Expr.Sum{T}"/>, etc.
/// </summary>
/// <example>
/// <code>
/// Expr.Count().Gt().Value(2)                  // HAVING COUNT(*) > 2
/// Expr.Sum&lt;Order&gt;(o => o.Total).Ge().Value(500)  // HAVING SUM("Total") >= 500
/// </code>
/// </example>
public sealed class AggregateExprBuilder
{
    private readonly string        _aggFunc;
    private readonly PropertyInfo? _property;
    private readonly Type?         _entityType;

    internal AggregateExprBuilder(string aggFunc, PropertyInfo? property, Type? entityType)
    {
        _aggFunc    = aggFunc;
        _property   = property;
        _entityType = entityType;
    }

    // ── Comparison operators ──────────────────────────────────────────────

    /// <summary>Aggregate = value.</summary>
    public AggregateValueExprBuilder Eq()  => Build(ComparisonOp.Eq);
    /// <summary>Aggregate != value.</summary>
    public AggregateValueExprBuilder Neq() => Build(ComparisonOp.Neq);
    /// <summary>Aggregate &gt; value.</summary>
    public AggregateValueExprBuilder Gt()  => Build(ComparisonOp.Gt);
    /// <summary>Aggregate &gt;= value.</summary>
    public AggregateValueExprBuilder Ge()  => Build(ComparisonOp.Ge);
    /// <summary>Aggregate &lt; value.</summary>
    public AggregateValueExprBuilder Lt()  => Build(ComparisonOp.Lt);
    /// <summary>Aggregate &lt;= value.</summary>
    public AggregateValueExprBuilder Le()  => Build(ComparisonOp.Le);

    private AggregateValueExprBuilder Build(ComparisonOp op)
        => new(_aggFunc, _property, _entityType, op);
}

/// <summary>
/// Completes an aggregate comparison by accepting the comparison value.
/// </summary>
public sealed class AggregateValueExprBuilder
{
    private readonly string        _aggFunc;
    private readonly PropertyInfo? _property;
    private readonly Type?         _entityType;
    private readonly ComparisonOp  _op;

    internal AggregateValueExprBuilder(
        string aggFunc, PropertyInfo? property, Type? entityType, ComparisonOp op)
    {
        _aggFunc    = aggFunc;
        _property   = property;
        _entityType = entityType;
        _op         = op;
    }

    /// <summary>Completes the aggregate comparison with the specified value.</summary>
    public IConditionExpr Value(object? value)
        => new AggregateConditionExpr(_aggFunc, _property, _entityType, _op, [value]);
}
