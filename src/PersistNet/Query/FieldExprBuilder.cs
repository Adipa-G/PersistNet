using System;
using System.Reflection;

namespace PersistNet.Query;

/// <summary>
/// Fluent builder returned by <see cref="Expr.Field{T}"/>. Exposes comparison
/// operators that each return either a <see cref="ValueExprBuilder{T}"/> (for
/// operators that need a value) or an <see cref="IConditionExpr"/> directly (for
/// null checks).
/// </summary>
public sealed class FieldExprBuilder<T>
{
    private readonly PropertyInfo _property;

    internal FieldExprBuilder(PropertyInfo property) => _property = property;

    // ── Single-value operators ────────────────────────────────────────────

    /// <summary>Column = value.</summary>
    public ValueExprBuilder<T> Eq()      => Op(ComparisonOp.Eq);
    /// <summary>Column != value.</summary>
    public ValueExprBuilder<T> Neq()     => Op(ComparisonOp.Neq);
    /// <summary>Column &gt; value.</summary>
    public ValueExprBuilder<T> Gt()      => Op(ComparisonOp.Gt);
    /// <summary>Column &gt;= value.</summary>
    public ValueExprBuilder<T> Ge()      => Op(ComparisonOp.Ge);
    /// <summary>Column &lt; value.</summary>
    public ValueExprBuilder<T> Lt()      => Op(ComparisonOp.Lt);
    /// <summary>Column &lt;= value.</summary>
    public ValueExprBuilder<T> Le()      => Op(ComparisonOp.Le);
    /// <summary>Column LIKE value (include % wildcards yourself).</summary>
    public ValueExprBuilder<T> Like()    => Op(ComparisonOp.Like);

    // ── Multi-value operators ─────────────────────────────────────────────

    /// <summary>Column BETWEEN lo AND hi — call <c>.Values(lo, hi)</c> next.</summary>
    public ValueExprBuilder<T> Between() => Op(ComparisonOp.Between);

    /// <summary>Column IN (v1, v2, ...) — call <c>.Values(...)</c> next.</summary>
    public ValueExprBuilder<T> In()      => Op(ComparisonOp.In);

    // ── Self-contained null checks ────────────────────────────────────────

    /// <summary>Column IS NULL.</summary>
    public IConditionExpr IsNull()    => new NullCheckExpr(_property, typeof(T), isNull: true);
    /// <summary>Column IS NOT NULL.</summary>
    public IConditionExpr IsNotNull() => new NullCheckExpr(_property, typeof(T), isNull: false);

    // ── Internal ─────────────────────────────────────────────────────────

    private ValueExprBuilder<T> Op(ComparisonOp op) => new(_property, op);
}
