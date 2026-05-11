using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

/// <summary>
/// SQLite implementation of <see cref="IDbPersistence"/>.
/// <para>
/// SQLite supports ANSI double-quote identifier quoting and row-value constructor
/// WHERE IN clauses for composite keys, so all SQL generation is inherited from
/// <see cref="AnsiSqlPersistenceBase"/> without any overrides.
/// </para>
/// </summary>
internal sealed class SqlitePersistence : AnsiSqlPersistenceBase
{
    internal SqlitePersistence(DbConnection connection, DbTransaction? transaction = null, ILogger? logger = null)
        : base(connection, transaction, logger) { }

    /// <summary>
    /// <c>Microsoft.Data.Sqlite</c> 8.x bundles SQLite ≥ 3.32 whose
    /// <c>SQLITE_LIMIT_VARIABLE_NUMBER</c> is 32766.  Use 32000 as a safe ceiling.
    /// </summary>
    protected override int MaxParameterBatchSize => 32000;

    protected override async Task<object?> GetLastInsertedKeyAsync(CancellationToken ct)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return await cmd.ExecuteScalarAsync(ct);
    }
}
