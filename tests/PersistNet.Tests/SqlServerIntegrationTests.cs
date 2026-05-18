using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet;
using PersistNet.DbInfo;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Integration tests for SQL Server's <c>OUTPUT INSERTED</c> batch-insert path.
/// Tests are silently skipped (pass vacuously) when <c>(localdb)\MSSQLLocalDB</c>
/// is not available on the machine, so CI environments without LocalDB are unaffected.
/// </summary>
public class SqlServerIntegrationTests : IAsyncLifetime
{
    private const string MasterConnStr =
        @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=SSPI;Connect Timeout=5";

    private static bool? _localDbAvailable;
    private string?      _dbName;
    private string?      _connStr;

    // ── Entity fixture ───────────────────────────────────────────────────────

    [TableInfo(TableName = "ss_items")]
    private class SsItem
    {
        [ColumnInfo(Key = true, AutoIncrement = true)] public int    Id   { get; set; }
        [ColumnInfo]                                   public string Name { get; set; } = "";
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (!await IsLocalDbAvailableAsync())
            return;

        _dbName  = $"PersistNetTest_{DateTime.UtcNow.Ticks}";
        _connStr = $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={_dbName};Integrated Security=SSPI";

        await using var master = new SqlConnection(MasterConnStr);
        await master.OpenAsync();
        using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_dbName}]";
        await create.ExecuteNonQueryAsync();

        await using var db = new SqlConnection(_connStr);
        await db.OpenAsync();
        using var schema = db.CreateCommand();
        schema.CommandText =
            "CREATE TABLE ss_items " +
            "(Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, Name NVARCHAR(200) NOT NULL)";
        await schema.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dbName is null) return;
        try
        {
            await using var conn = new SqlConnection(MasterConnStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_dbName}]";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort cleanup */ }
    }

    private static async Task<bool> IsLocalDbAvailableAsync()
    {
        if (_localDbAvailable.HasValue)
            return _localDbAvailable.Value;
        try
        {
            await using var conn = new SqlConnection(MasterConnStr);
            await conn.OpenAsync();
            _localDbAvailable = true;
        }
        catch { _localDbAvailable = false; }
        return _localDbAvailable.Value;
    }

    private TransactionFactory Factory() =>
        new(_connStr!, SqlClientFactory.Instance, DbProvider.SqlServer,
            NullLogger<TransactionFactory>.Instance);

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_SingleAutoIncrementEntity_IdHydratedViaOutputInserted()
    {
        if (_connStr is null) return; // LocalDB not available

        var item = new SsItem { Name = "First" };
        await using var txn = await Factory().OpenTransactionAsync();
        txn.Save(item);
        await txn.CommitAsync();

        Assert.NotEqual(0, item.Id);

        // Confirm the ID matches what the DB actually assigned.
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM ss_items WHERE Name = 'First'";
        var dbId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(dbId, item.Id);
    }

    [Fact]
    public async Task Save_MultipleAutoIncrementEntities_AllIdsHydratedInSingleBatch()
    {
        if (_connStr is null) return; // LocalDB not available

        var items = Enumerable.Range(1, 5)
            .Select(i => new SsItem { Name = $"Item{i}" })
            .ToArray();

        await using var txn = await Factory().OpenTransactionAsync();
        foreach (var item in items)
            txn.Save(item);
        await txn.CommitAsync();

        // All IDs must be non-zero and unique — hydrated via OUTPUT INSERTED.
        Assert.All(items, item => Assert.NotEqual(0, item.Id));
        Assert.Equal(5, items.Select(i => i.Id).Distinct().Count());

        // Verify IDs match actual DB rows.
        await using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM ss_items ORDER BY Id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var dbRows = new System.Collections.Generic.List<(int Id, string Name)>();
        while (await reader.ReadAsync())
            dbRows.Add((reader.GetInt32(0), reader.GetString(1)));

        Assert.Equal(5, dbRows.Count);
        foreach (var item in items)
        {
            var match = dbRows.Single(r => r.Name == item.Name);
            Assert.Equal(match.Id, item.Id);
        }
    }
}

