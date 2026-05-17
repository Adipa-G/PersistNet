using System;
using System.Linq.Expressions;
using PersistNet.DbInfo;

namespace PersistNet.Query;

/// <summary>
/// Represents a single JOIN clause accumulated by <see cref="ISelectQuery{T}.InnerJoin{TJoin}"/>
/// or <see cref="ISelectQuery{T}.LeftJoin{TJoin}"/>.
/// </summary>
internal sealed class JoinClause
{
    internal Type             JoinedType   { get; }
    internal Table            JoinedTable  { get; }
    /// <summary>
    /// Two-parameter lambda: <c>(t, j) => t.FK == j.Id</c>.
    /// <c>Parameters[0]</c> is the primary entity; <c>Parameters[1]</c> is <typeparamref name="TJoin"/>.
    /// </summary>
    internal LambdaExpression Condition    { get; }
    /// <summary>SQL keyword, e.g. <c>"INNER JOIN"</c> or <c>"LEFT JOIN"</c>.</summary>
    internal string           JoinKeyword  { get; }

    internal JoinClause(Type joinedType, Table joinedTable, LambdaExpression condition, string joinKeyword)
    {
        JoinedType  = joinedType;
        JoinedTable = joinedTable;
        Condition   = condition;
        JoinKeyword = joinKeyword;
    }
}
