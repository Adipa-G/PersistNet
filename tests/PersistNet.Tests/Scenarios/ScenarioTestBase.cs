using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Shared base for scenario integration tests.
/// Each concrete test class gets its own in-memory SQLite connection and factory,
/// ensuring full test isolation.
/// </summary>
public abstract class ScenarioTestBase : IAsyncDisposable
{
    protected readonly SqliteConnection Connection;
    protected readonly TransactionFactory Factory;

    protected ScenarioTestBase()
    {
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();

        // Enable FK constraint enforcement (SQLite disables it by default).
        using (var pragma = Connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }

        Factory = new TransactionFactory(
            Connection, DbProvider.SQLite,
            NullLogger<TransactionFactory>.Instance);
    }

    /// <summary>
    /// Creates schema for the given entity types using <see cref="SchemaUpgrader"/>.
    /// Tables are generated with proper column definitions and FK constraints derived
    /// from the entity relationship attributes.
    /// </summary>
    protected Task CreateSchemaAsync(params Type[] entityTypes)
        => SchemaUpgrader.ForTypes(Connection, DbProvider.SQLite, entityTypes).ApplyAsync();

    /// <summary>Executes a DDL or DML statement with no result.</summary>
    protected async Task ExecAsync(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns the row count for a table.</summary>
    protected async Task<long> CountAsync(string tableName)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Returns a single scalar value, or <c>null</c> when the DB returns NULL.</summary>
    protected async Task<object?> ScalarAsync(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
