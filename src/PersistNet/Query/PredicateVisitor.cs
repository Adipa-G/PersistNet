using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using PersistNet.DbInfo;

namespace PersistNet.Query;

/// <summary>
/// Walks a C# expression tree and emits parameterized SQL fragments.
/// Supports the common operators used in <c>.Where(p => ...)</c> lambda predicates,
/// including two-parameter JOIN conditions such as <c>(t, j) => t.FK == j.Id</c>.
/// </summary>
/// <remarks>
/// Unsupported expressions throw <see cref="NotSupportedException"/> with a hint to
/// use the <see cref="Expr"/> builder overload instead.
/// </remarks>
internal sealed class PredicateVisitor
{
    private readonly CompileContext                ctx;
    private readonly Func<string, string>          _quote;
    /// <summary>
    /// Fallback primary table used when no alias map is present (single-table queries).
    /// May be null when the alias map covers all referenced types (e.g. JOIN conditions).
    /// </summary>
    private readonly Table?                        _primaryTable;

    /// <param name="ctx">Compile context (parameter list + optional alias map).</param>
    /// <param name="quote">Provider quoting function.</param>
    /// <param name="primaryTable">
    /// Fallback table used in single-table mode when the alias map is empty.
    /// Pass <c>null</c> only when the alias map covers all parameter types (JOIN conditions).
    /// </param>
    internal PredicateVisitor(
        CompileContext       ctx,
        Func<string, string> quote,
        Table?               primaryTable = null)
    {
        this.ctx     = ctx;
        _quote        = quote;
        _primaryTable = primaryTable;
    }

    // â”€â”€ Entry point â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    internal string Visit(Expression expr)
    {
        return expr switch
        {
            BinaryExpression b       => VisitBinary(b),
            UnaryExpression  u       => VisitUnary(u),
            MemberExpression m       => VisitMember(m),
            ConstantExpression c     => VisitConstant(c.Value),
            MethodCallExpression mc  => VisitMethodCall(mc),
            // Capture typed-expression wrappers (e.g. Expression<Func<T,bool>> body)
            LambdaExpression lam     => Visit(lam.Body),
            _ => throw new NotSupportedException(
                $"Expression node '{expr.NodeType}' is not supported in a lambda WHERE clause. " +
                "Use .Where(Expr.Field<T>(f => f.Property)...) for complex conditions.")
        };
    }

    // â”€â”€ Binary â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string VisitBinary(BinaryExpression b)
    {
        // null comparisons: p.X == null â†’ IS NULL  /  p.X != null â†’ IS NOT NULL
        if (b.NodeType == ExpressionType.Equal && IsNullConst(b.Right))
            return $"{Visit(b.Left)} IS NULL";
        if (b.NodeType == ExpressionType.Equal && IsNullConst(b.Left))
            return $"{Visit(b.Right)} IS NULL";
        if (b.NodeType == ExpressionType.NotEqual && IsNullConst(b.Right))
            return $"{Visit(b.Left)} IS NOT NULL";
        if (b.NodeType == ExpressionType.NotEqual && IsNullConst(b.Left))
            return $"{Visit(b.Right)} IS NOT NULL";

        var op = b.NodeType switch
        {
            ExpressionType.Equal              => "=",
            ExpressionType.NotEqual           => "!=",
            ExpressionType.GreaterThan        => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan           => "<",
            ExpressionType.LessThanOrEqual    => "<=",
            ExpressionType.AndAlso            => "AND",
            ExpressionType.OrElse             => "OR",
            _ => throw new NotSupportedException(
                $"Binary operator '{b.NodeType}' is not supported. " +
                "Use .Where(Expr.Field<T>(f => f.Property)...) for complex conditions.")
        };

        // Logical: wrap each side in parens to preserve precedence
        if (b.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
            return $"({Visit(b.Left)}) {op} ({Visit(b.Right)})";

        return $"{Visit(b.Left)} {op} {Visit(b.Right)}";
    }

    // â”€â”€ Unary â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string VisitUnary(UnaryExpression u)
    {
        // Strip boxing/conversion casts
        if (u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            return Visit(u.Operand);

        if (u.NodeType == ExpressionType.Not)
            return $"NOT ({Visit(u.Operand)})";

        throw new NotSupportedException(
            $"Unary operator '{u.NodeType}' is not supported in a lambda WHERE clause.");
    }

    // â”€â”€ Member â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string VisitMember(MemberExpression m)
    {
        // Entity property access on a lambda parameter â†’ column name (possibly aliased)
        if (m.Expression is ParameterExpression paramExpr)
        {
            if (m.Member is not PropertyInfo pi)
                throw new NotSupportedException($"Only property access is supported (got field '{m.Member.Name}').");

            // Multi-table mode: look up the table and alias by the parameter's C# type.
            // This handles both primary entity params (t0) and joined entity params (t1, t2, â€¦)
            // in two-parameter JOIN condition lambdas and Where<TJoin> predicates.
            if (ctx.AliasMap.TryGetValue(paramExpr.Type, out var entry))
            {
                var col = entry.Table.Columns.FirstOrDefault(c => c.Property == pi)
                    ?? throw new InvalidOperationException(
                        $"Property '{pi.Name}' on '{paramExpr.Type.Name}' was not found in " +
                        $"table '{entry.Table.Name}'. Ensure it has a [ColumnInfo] attribute.");
                return $"{entry.Alias}.{_quote(col.ColumnName)}";
            }

            // Single-table mode (no alias map): use the primary table directly.
            var table = _primaryTable
                ?? throw new InvalidOperationException(
                    $"Cannot resolve property '{pi.Name}' â€” no table context for type '{paramExpr.Type.Name}'.");
            var colFallback = table.Columns.FirstOrDefault(c => c.Property == pi)
                ?? throw new InvalidOperationException(
                    $"Property '{pi.Name}' on '{table.EntityType.Name}' was not found in " +
                    $"table '{table.Name}'. Ensure it has a [ColumnInfo] attribute.");
            return _quote(colFallback.ColumnName);
        }

        // Closure / local variable â€” evaluate at query-build time
        return VisitConstant(EvaluateMember(m));
    }

    // â”€â”€ Constant â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string VisitConstant(object? value)
    {
        var pName = ctx.NextParam();
        ctx.Parameters.Add((pName, value));
        return pName;
    }

    // â”€â”€ Method calls â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string VisitMethodCall(MethodCallExpression mc)
    {
        // string.Contains(val) â†’ col LIKE '%val%'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "Contains"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = ctx.NextParam();
            ctx.Parameters.Add((pName, $"%{val}%"));
            return $"{colSql} LIKE {pName}";
        }

        // string.StartsWith(val) â†’ col LIKE 'val%'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "StartsWith"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = ctx.NextParam();
            ctx.Parameters.Add((pName, $"{val}%"));
            return $"{colSql} LIKE {pName}";
        }

        // string.EndsWith(val) â†’ col LIKE '%val'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "EndsWith"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = ctx.NextParam();
            ctx.Parameters.Add((pName, $"%{val}"));
            return $"{colSql} LIKE {pName}";
        }

        // list.Contains(p.Field) â†’ col IN (@p0, @p1, ...)
        // or Enumerable.Contains(list, p.Field)
        if (mc.Method.Name == "Contains")
        {
            Expression collectionExpr;
            Expression memberExpr;

            if (mc.Object is not null)
            {
                // instance method: list.Contains(p.Field)
                collectionExpr = mc.Object;
                memberExpr     = mc.Arguments[0];
            }
            else if (mc.Arguments.Count == 2)
            {
                // static: Enumerable.Contains(list, p.Field)
                collectionExpr = mc.Arguments[0];
                memberExpr     = mc.Arguments[1];
            }
            else
            {
                throw new NotSupportedException(
                    $"Unsupported Contains() call signature: {mc}");
            }

            var colSql = Visit(memberExpr);
            var values = EvaluateExpression(collectionExpr) as IEnumerable
                ?? throw new InvalidOperationException(
                    $"Cannot evaluate collection in Contains() call: {collectionExpr}");

            var paramNames = new List<string>();
            foreach (var item in values)
            {
                var pName = ctx.NextParam();
                ctx.Parameters.Add((pName, item));
                paramNames.Add(pName);
            }

            if (paramNames.Count == 0)
                return "1=0"; // empty IN â†’ always false

            return $"{colSql} IN ({string.Join(", ", paramNames)})";
        }

        throw new NotSupportedException(
            $"Method call '{mc.Method.DeclaringType?.Name}.{mc.Method.Name}' is not supported " +
            "in a lambda WHERE clause. Use .Where(Expr.Field<T>(f => f.Property)...) instead.");
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static bool IsNullConst(Expression e)
        => e is ConstantExpression { Value: null }
           || (e is UnaryExpression { NodeType: ExpressionType.Convert } u && IsNullConst(u.Operand));

    private static object? EvaluateMember(MemberExpression m)
        => EvaluateExpression(m);

    private static object? EvaluateExpression(Expression expr)
    {
        // Fast path for constants
        if (expr is ConstantExpression c) return c.Value;

        // General: compile and invoke (handles closures, field accesses, etc.)
        try
        {
            return Expression.Lambda(expr).Compile().DynamicInvoke();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not evaluate expression '{expr}' as a constant value. " +
                "Ensure any captured variables are accessible at query-build time.", ex);
        }
    }
}

