using Microsoft.Extensions.Logging;
using System.Data.Common;

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
}
