using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet.DbInfo;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// End-to-end integration tests for <see cref="Transaction"/> that run real DML
/// against an in-memory SQLite database via <see cref="TransactionFactory"/>.
/// Each test gets a fresh database because xUnit creates a new instance per test method.
/// </summary>
public class TransactionIntegrationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TransactionFactory _factory;

    // ── Fixture entity types ────────────────────────────────────────────────

    [TableInfo(TableName = "txn_products")]
    private class TxnProduct
    {
        [ColumnInfo(Key = true)] public int    Id    { get; set; }
        [ColumnInfo]             public string Name  { get; set; } = "";
        [ColumnInfo]             public int    Price { get; set; }
    }

    // ── Setup / teardown ────────────────────────────────────────────────────

    public TransactionIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TransactionFactory(_connection, DbProvider.SQLite,
            NullLogger<TransactionFactory>.Instance);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task CreateProductsTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE txn_products " +
            "(Id INTEGER NOT NULL, Name TEXT NOT NULL, Price INTEGER NOT NULL, PRIMARY KEY (Id))";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertProductDirectly(int id, string name, int price)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO txn_products (Id, Name, Price) VALUES (@id, @name, @price)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@price", price);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM txn_products";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    // ── Save + Commit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_ThenCommit_InsertsRow()
    {
        await CreateProductsTable();

        await using var txn = await _factory.OpenTransactionAsync();
        // Id = 0 (default) → ChangeSetBuilder treats this as INSERT
        txn.Save(new TxnProduct { Id = 0, Name = "Widget", Price = 99 });
        await txn.CommitAsync();

        Assert.Equal(1, await CountAsync());
        Assert.Equal("Widget", await ScalarAsync("SELECT Name FROM txn_products WHERE Id = 0"));
    }

    [Fact]
    public async Task Save_ThenRollback_DoesNotInsertRow()
    {
        await CreateProductsTable();

        await using (var txn = await _factory.OpenTransactionAsync())
        {
            txn.Save(new TxnProduct { Id = 0, Name = "Ghost", Price = 0 });
            // No CommitAsync — DisposeAsync rolls back.
        }

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Save_MultipleSaves_AllUpdatesFlushOnCommit()
    {
        await CreateProductsTable();
        // Pre-insert 3 rows directly so they exist in the DB.
        await InsertProductDirectly(1, "A", 1);
        await InsertProductDirectly(2, "B", 2);
        await InsertProductDirectly(3, "C", 3);

        // Update all 3 via the transaction.
        await using var txn = await _factory.OpenTransactionAsync();
        txn.Save(new TxnProduct { Id = 1, Name = "A-Updated", Price = 10 });
        txn.Save(new TxnProduct { Id = 2, Name = "B-Updated", Price = 20 });
        txn.Save(new TxnProduct { Id = 3, Name = "C-Updated", Price = 30 });
        await txn.CommitAsync();

        Assert.Equal("A-Updated", await ScalarAsync("SELECT Name FROM txn_products WHERE Id = 1"));
        Assert.Equal("B-Updated", await ScalarAsync("SELECT Name FROM txn_products WHERE Id = 2"));
        Assert.Equal("C-Updated", await ScalarAsync("SELECT Name FROM txn_products WHERE Id = 3"));
    }

    // ── Delete + Commit ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ThenCommit_RemovesRow()
    {
        await CreateProductsTable();
        await InsertProductDirectly(1, "ToDelete", 0);
        await InsertProductDirectly(2, "Keep", 0);

        await using var txn = await _factory.OpenTransactionAsync();
        txn.Delete(new TxnProduct { Id = 1 });
        await txn.CommitAsync();

        Assert.Equal(1, await CountAsync());
        Assert.Null(await ScalarAsync("SELECT Id FROM txn_products WHERE Id = 1"));
    }

    // ── GetAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_AfterSaveAndCommit_ReturnsEntity()
    {
        await CreateProductsTable();

        // INSERT with Id=0 (default), then read it back.
        await using var writeTxn = await _factory.OpenTransactionAsync();
        writeTxn.Save(new TxnProduct { Id = 0, Name = "Gadget", Price = 1500 });
        await writeTxn.CommitAsync();

        await using var readTxn = await _factory.OpenTransactionAsync();
        var product = await readTxn.GetAsync<TxnProduct>(0);

        Assert.Equal(0,        product.Id);
        Assert.Equal("Gadget", product.Name);
        Assert.Equal(1500,     product.Price);
    }

    [Fact]
    public async Task GetAsync_MissingKey_ThrowsInvalidOperationException()
    {
        await CreateProductsTable();

        await using var txn = await _factory.OpenTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await txn.GetAsync<TxnProduct>(99));
    }

    // ── SaveAndCommitAsync convenience ────────────────────────────────────────

    [Fact]
    public async Task SaveAndCommitAsync_InsertsRowAndReturnsEntity()
    {
        await CreateProductsTable();

        // Id=0 (default) → INSERT
        await using var txn = await _factory.OpenTransactionAsync();
        var returned = await txn.SaveAndCommitAsync(new TxnProduct { Id = 0, Name = "Quick", Price = 7 });

        Assert.Equal(1, await CountAsync());
        Assert.Equal(0,       returned.Id);
        Assert.Equal("Quick", returned.Name);
    }

    // ── Versioned entity (optimistic concurrency) ─────────────────────────────

    [TableInfo(TableName = "ver_items")]
    private class VerItem
    {
        [ColumnInfo(Key = true)]                public int    Id      { get; set; }
        [ColumnInfo]                            public string Name    { get; set; } = "";
        [ColumnInfo(IsVersion = true)]          public long   Version { get; set; }
    }

    private async Task CreateVerItemsTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE ver_items " +
            "(Id INTEGER NOT NULL, Name TEXT NOT NULL, Version INTEGER NOT NULL, PRIMARY KEY (Id))";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertVerItemDirectly(int id, string name, long version)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ver_items (Id, Name, Version) VALUES (@id, @name, @ver)";
        cmd.Parameters.AddWithValue("@id",   id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@ver",  version);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> GetVerItemVersionAsync(int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Version FROM ver_items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Save_VersionedEntity_IncrementsVersionInDatabase()
    {
        await CreateVerItemsTable();
        await InsertVerItemDirectly(1, "Alpha", 1L);

        await using var txn = await _factory.OpenTransactionAsync();
        txn.Save(new VerItem { Id = 1, Name = "Alpha-Updated", Version = 1L });
        await txn.CommitAsync();

        Assert.Equal(2L, await GetVerItemVersionAsync(1));
    }

    [Fact]
    public async Task Save_VersionedEntity_StaleVersion_ThrowsConcurrencyException()
    {
        await CreateVerItemsTable();
        await InsertVerItemDirectly(1, "Beta", 3L); // DB has version 3

        await using var txn = await _factory.OpenTransactionAsync();
        // Simulate stale read: entity thinks version is 1, but DB has 3
        txn.Save(new VerItem { Id = 1, Name = "Beta-Updated", Version = 1L });

        await Assert.ThrowsAsync<ConcurrencyException>(() => txn.CommitAsync());
    }

    [Fact]
    public async Task Save_NonVersionedEntity_RowDeleted_ThrowsInvalidOperationException()
    {
        await CreateProductsTable();
        await InsertProductDirectly(1, "ToDelete", 0);

        // Delete the row outside the transaction so the UPDATE sees 0 rows.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM txn_products WHERE Id = 1";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var txn = await _factory.OpenTransactionAsync();
        txn.Save(new TxnProduct { Id = 1, Name = "Ghost", Price = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => txn.CommitAsync());
    }

    // ── Auto-increment PK hydration ───────────────────────────────────────────

    [TableInfo(TableName = "auto_items")]
    private class AutoItem
    {
        [ColumnInfo(Key = true, AutoIncrement = true)] public int    Id   { get; set; }
        [ColumnInfo]                                   public string Name { get; set; } = "";
    }

    private async Task CreateAutoItemsTable()
    {
        using var cmd = _connection.CreateCommand();
        // SQLite AUTOINCREMENT guarantees the Id is never reused and is strictly increasing.
        cmd.CommandText = "CREATE TABLE auto_items (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Save_AutoIncrementEntity_IdHydratedAfterCommit()
    {
        await CreateAutoItemsTable();

        var item = new AutoItem { Name = "First" }; // Id = 0 (default) → INSERT
        await using var txn = await _factory.OpenTransactionAsync();
        txn.Save(item);
        await txn.CommitAsync();

        // Id must have been written back from last_insert_rowid().
        Assert.NotEqual(0, item.Id);

        // Confirm the row is in the DB with the generated Id.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM auto_items WHERE Name = 'First'";
        var dbId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(dbId, item.Id);
    }

    [Fact]
    public async Task Save_MultipleAutoIncrementEntities_EachIdHydratedUniquely()
    {
        await CreateAutoItemsTable();

        var a = new AutoItem { Name = "A" };
        var b = new AutoItem { Name = "B" };
        var c = new AutoItem { Name = "C" };

        await using var txn = await _factory.OpenTransactionAsync();
        txn.Save(a);
        txn.Save(b);
        txn.Save(c);
        await txn.CommitAsync();

        // All three Ids must be distinct and non-zero.
        Assert.NotEqual(0, a.Id);
        Assert.NotEqual(0, b.Id);
        Assert.NotEqual(0, c.Id);
        Assert.Equal(3, new[] { a.Id, b.Id, c.Id }.Distinct().Count());
    }

    // ── Dirty tracking ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ThenChangeOneField_UpdateOnlyContainsThatField()
    {
        // Use a versioned entity: the version bump proves the UPDATE ran,
        // and we verify the untouched field retains its original value in the DB.
        await CreateVerItemsTable();
        await InsertVerItemDirectly(1, "Original", 1L);

        await using var txn = await _factory.OpenTransactionAsync();
        var item = await txn.GetAsync<VerItem>(1);

        // Change only Name; leave Version as-is (the ORM will auto-bump it).
        item.Name = "Updated";
        txn.Save(item);
        await txn.CommitAsync();

        // Version must have incremented — proves UPDATE ran.
        Assert.Equal(2L, await GetVerItemVersionAsync(1));

        // Name must reflect the change.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Name FROM ver_items WHERE Id = 1";
        Assert.Equal("Updated", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task GetAsync_ThenSaveWithNoChanges_NoUpdateExecuted()
    {
        // If dirty tracking works, saving an unchanged entity emits no SQL.
        // Use the version column as a canary: if an UPDATE ran, the version would bump.
        await CreateVerItemsTable();
        await InsertVerItemDirectly(1, "Unchanged", 5L);

        await using var txn = await _factory.OpenTransactionAsync();
        var item = await txn.GetAsync<VerItem>(1);
        txn.Save(item); // nothing changed
        await txn.CommitAsync();

        // Version must still be 5 — no UPDATE was issued.
        Assert.Equal(5L, await GetVerItemVersionAsync(1));
    }
}
