using Microsoft.Extensions.Logging;
using PersistNet.Entities;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    protected override async Task<object?> GetLastInsertedKeyAsync(CancellationToken ct)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT SCOPE_IDENTITY()";
        var result = await cmd.ExecuteScalarAsync(ct);
        // SCOPE_IDENTITY() returns DBNull when no identity insert has occurred in scope.
        return result is DBNull ? null : result;
    }

    // ── Batch INSERT with OUTPUT INSERTED ────────────────────────────────────

    /// <summary>
    /// Overrides the base row-by-row path for auto-increment entities.
    /// Emits a single <c>INSERT … OUTPUT INSERTED.[keyCol] VALUES (r1),(r2),…</c>
    /// statement per chunk, returning all generated keys in one round trip.
    /// Falls back to the base implementation when no key column is known.
    /// </summary>
    public override async Task ExecuteInsertAsync(MultiRowInsert insert, CancellationToken ct = default)
    {
        // No callbacks → efficient base batch path; no hydration needed.
        // No key column name → fall back to row-by-row SCOPE_IDENTITY() path.
        if (insert.KeyCallbacks is null || insert.AutoIncrKeyColumn is null)
        {
            await base.ExecuteInsertAsync(insert, ct);
            return;
        }

        var paramsPerRow    = Math.Max(1, insert.Columns.Count);
        var maxRowsPerChunk = Math.Max(1, MaxParameterBatchSize / paramsPerRow);

        var callbackOffset = 0;
        foreach (var chunkRows in insert.ValueRows.Chunk(maxRowsPerChunk))
        {
            var (sql, parameters) = BuildInsertWithOutputSql(insert, chunkRows);

            using var cmd = CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rowIndex = 0;
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetValue(0);
                var callback = insert.KeyCallbacks[callbackOffset + rowIndex];
                if (callback is not null && key is not DBNull)
                    callback(key);
                rowIndex++;
            }

            callbackOffset += chunkRows.Length;
        }
    }

    /// <summary>
    /// Builds an <c>INSERT INTO [t] ([c1],[c2]) OUTPUT INSERTED.[keyCol] VALUES (…)</c>
    /// statement for the supplied chunk of value rows.  Parameter names are @p0-based.
    /// </summary>
    internal (string Sql, List<(string Name, object? Value)> Parameters)
        BuildInsertWithOutputSql(
            MultiRowInsert insert,
            IReadOnlyList<IReadOnlyList<object?>> chunkRows)
    {
        var parameters = new List<(string Name, object? Value)>();
        var idx = 0;

        var cols     = string.Join(", ", insert.Columns.Select(QuoteIdentifier));
        var keyCol   = QuoteIdentifier(insert.AutoIncrKeyColumn!);
        var rowParts = new List<string>();

        foreach (var row in chunkRows)
        {
            var pNames = new List<string>();
            foreach (var value in row)
            {
                var pName = $"@p{idx++}";
                parameters.Add((pName, value));
                pNames.Add(pName);
            }
            rowParts.Add($"({string.Join(", ", pNames)})");
        }

        var sql = $"INSERT INTO {QualifiedTable(insert.TableName, insert.Schema)} " +
                  $"({cols}) OUTPUT INSERTED.{keyCol} " +
                  $"VALUES {string.Join(", ", rowParts)}";
        return (sql, parameters);
    }
}
