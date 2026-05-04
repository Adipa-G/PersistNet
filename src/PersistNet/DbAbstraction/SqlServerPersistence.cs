using Microsoft.Extensions.Logging;
using PersistNet.Entities;
using System.Collections.Generic;
using System.Data.Common;

namespace PersistNet.DbAbstraction;

/// <summary>
/// SQL Server implementation of <see cref="IDbPersistence"/>.
/// <para>
/// Differences from the ANSI base:
/// <list type="bullet">
///   <item>Identifiers are quoted with square brackets: <c>[name]</c>.</item>
///   <item>
///     Composite-key WHERE clauses use OR-predicate chains instead of ANSI
///     row-value constructors, because SQL Server does not support the syntax
///     <c>(k1, k2) IN ((v1, v2), …)</c>.
///   </item>
/// </list>
/// </para>
/// </summary>
internal sealed class SqlServerPersistence : AnsiSqlPersistenceBase
{
    internal SqlServerPersistence(DbConnection connection, DbTransaction? transaction = null, ILogger? logger = null)
        : base(connection, transaction, logger) { }

    protected override string QuoteIdentifier(string name) => $"[{name}]";

    /// <summary>
    /// Single-key WHERE clause is delegated to the base (IN clause is standard SQL).
    /// Composite-key WHERE clause is expressed as OR-predicate chains:
    /// <c>([k1]=@p0 AND [k2]=@p1) OR ([k1]=@p2 AND [k2]=@p3)</c>
    /// </summary>
    protected override string BuildKeyWhereClause(
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyList<object?>> keyValues,
        List<(string Name, object? Value)> parameters,
        ref int idx)
    {
        if (keyColumns.Count == 1)
            return base.BuildKeyWhereClause(keyColumns, keyValues, parameters, ref idx);

        // Composite key: OR chain of per-row AND predicates.
        var predicates = new List<string>();
        foreach (var kv in keyValues)
        {
            var andParts = new List<string>();
            for (var i = 0; i < keyColumns.Count; i++)
            {
                var pName = $"@p{idx++}";
                parameters.Add((pName, kv[i]));
                andParts.Add($"{QuoteIdentifier(keyColumns[i])}={pName}");
            }
            predicates.Add($"({string.Join(" AND ", andParts)})");
        }
        return string.Join(" OR ", predicates);
    }
}
