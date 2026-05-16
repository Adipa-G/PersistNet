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
/// Supports the common operators used in <c>.Where(p => ...)</c> lambda predicates.
/// </summary>
/// <remarks>
/// Unsupported expressions throw <see cref="NotSupportedException"/> with a hint to
/// use the <see cref="Expr"/> builder overload instead.
/// </remarks>
internal sealed class PredicateVisitor
{
    private readonly Table                         _table;
    private readonly Func<string, string>          _quote;
    private readonly List<(string Name, object? Value)> _parameters;
    private readonly CompileContext                _ctx;

    internal PredicateVisitor(
        Table table,
        Func<string, string> quote,
        CompileContext ctx)
    {
        _table      = table;
        _quote      = quote;
        _parameters = ctx.Parameters;
        _ctx        = ctx;
    }

    // ── Entry point ───────────────────────────────────────────────────────

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

    // ── Binary ────────────────────────────────────────────────────────────

    private string VisitBinary(BinaryExpression b)
    {
        // null comparisons: p.X == null → IS NULL  /  p.X != null → IS NOT NULL
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

    // ── Unary ─────────────────────────────────────────────────────────────

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

    // ── Member ────────────────────────────────────────────────────────────

    private string VisitMember(MemberExpression m)
    {
        // Entity property access on the lambda parameter → column name
        if (m.Expression is ParameterExpression)
        {
            if (m.Member is not PropertyInfo pi)
                throw new NotSupportedException($"Only property access is supported (got field '{m.Member.Name}').");
            var col = _table.Columns.FirstOrDefault(c => c.Property == pi)
                ?? throw new InvalidOperationException(
                    $"Property '{pi.Name}' on '{_table.EntityType.Name}' was not found in table '{_table.Name}'. " +
                    "Ensure it has a [ColumnInfo] attribute.");
            return _quote(col.ColumnName);
        }

        // Closure / local variable — evaluate at query-build time
        return VisitConstant(EvaluateMember(m));
    }

    // Hack: VisitMember uses the table's entity type for error messages
    // (no generic type parameter on the class itself)

    // ── Constant ─────────────────────────────────────────────────────────

    private string VisitConstant(object? value)
    {
        var pName = _ctx.NextParam();
        _ctx.Parameters.Add((pName, value));
        return pName;
    }

    // ── Method calls ─────────────────────────────────────────────────────

    private string VisitMethodCall(MethodCallExpression mc)
    {
        // string.Contains(val) → col LIKE '%val%'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "Contains"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = _ctx.NextParam();
            _ctx.Parameters.Add((pName, $"%{val}%"));
            return $"{colSql} LIKE {pName}";
        }

        // string.StartsWith(val) → col LIKE 'val%'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "StartsWith"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = _ctx.NextParam();
            _ctx.Parameters.Add((pName, $"{val}%"));
            return $"{colSql} LIKE {pName}";
        }

        // string.EndsWith(val) → col LIKE '%val'
        if (mc.Method.DeclaringType == typeof(string) && mc.Method.Name == "EndsWith"
            && mc.Object is not null)
        {
            var colSql = Visit(mc.Object);
            var val    = EvaluateExpression(mc.Arguments[0])?.ToString() ?? "";
            var pName  = _ctx.NextParam();
            _ctx.Parameters.Add((pName, $"%{val}"));
            return $"{colSql} LIKE {pName}";
        }

        // list.Contains(p.Field) → col IN (@p0, @p1, ...)
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
                var pName = _ctx.NextParam();
                _ctx.Parameters.Add((pName, item));
                paramNames.Add(pName);
            }

            if (paramNames.Count == 0)
                return "1=0"; // empty IN → always false

            return $"{colSql} IN ({string.Join(", ", paramNames)})";
        }

        throw new NotSupportedException(
            $"Method call '{mc.Method.DeclaringType?.Name}.{mc.Method.Name}' is not supported " +
            "in a lambda WHERE clause. Use .Where(Expr.Field<T>(f => f.Property)...) instead.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
