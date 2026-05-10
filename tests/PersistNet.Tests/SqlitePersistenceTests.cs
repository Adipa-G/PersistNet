using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;
using PersistNet.Entities;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Integration tests for <see cref="SqlitePersistence"/> that execute real DML
/// against an in-memory SQLite database.  Each test gets a fresh database because
/// xUnit creates a new test-class instance per test method.
/// </summary>
public class SqlitePersistenceTests : IAsyncDisposable
{
    private readonly SqliteConnection  _connection;
    private readonly SqlitePersistence _persistence;

    // ── Fixture entity types ────────────────────────────────────────────────

    [TableInfo(TableName = "pst_products")]
    private class PstProduct
    {
        [ColumnInfo(Key = true)] public int     Id    { get; set; }
        [ColumnInfo]             public string  Name  { get; set; } = "";
        [ColumnInfo]             public int     Price { get; set; }   // int avoids float-precision issues
    }

    [TableInfo(TableName = "pst_lines")]
    private class PstLine
    {
        [ColumnInfo(Key = true)] public int OrderId { get; set; }
        [ColumnInfo(Key = true)] public int ItemId  { get; set; }
        [ColumnInfo]             public int Qty     { get; set; }
    }

    // ── Setup / teardown ────────────────────────────────────────────────────

    public SqlitePersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _persistence = new SqlitePersistence(_connection);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ── Table DDL helpers ───────────────────────────────────────────────────

    private async Task CreateProductsTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE pst_products " +
            "(Id INTEGER NOT NULL, Name TEXT NOT NULL, Price INTEGER NOT NULL, PRIMARY KEY (Id))";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateLinesTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE pst_lines " +
            "(OrderId INTEGER NOT NULL, ItemId INTEGER NOT NULL, Qty INTEGER NOT NULL, " +
            "PRIMARY KEY (OrderId, ItemId))";
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Query helpers ───────────────────────────────────────────────────────

    private async Task<int> CountAsync(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    // ── INSERT ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_SingleRowInsert_When_ExecuteInsertAsync_Then_RowAppearsInDatabase()
    {
        await CreateProductsTable();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[] { new object?[] { 1, "Widget", 999 } });

        await _persistence.ExecuteInsertAsync(insert);

        Assert.Equal(1, await CountAsync("pst_products"));
        Assert.Equal("Widget", await ScalarAsync("SELECT Name FROM pst_products WHERE Id = 1"));
    }

    [Fact]
    public async Task Given_MultipleRowInsert_When_ExecuteInsertAsync_Then_AllRowsInserted()
    {
        await CreateProductsTable();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[]
            {
                new object?[] { 1, "A", 100 },
                new object?[] { 2, "B", 200 },
                new object?[] { 3, "C", 300 },
            });

        await _persistence.ExecuteInsertAsync(insert);

        Assert.Equal(3, await CountAsync("pst_products"));
    }

    [Fact]
    public async Task Given_NullColumnValue_When_ExecuteInsertAsync_Then_StoredAsNull()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE pst_products " +
            "(Id INTEGER NOT NULL, Name TEXT, Price INTEGER NOT NULL, PRIMARY KEY (Id))";
        await cmd.ExecuteNonQueryAsync();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[] { new object?[] { 1, null, 0 } });

        await _persistence.ExecuteInsertAsync(insert);

        Assert.Null(await ScalarAsync("SELECT Name FROM pst_products WHERE Id = 1"));
    }

    // ── FindByKeyAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Given_ExistingRow_When_FindByKeyAsync_Then_ReturnsEntityWithCorrectValues()
    {
        await CreateProductsTable();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[] { new object?[] { 42, "Gadget", 1500 } });
        await _persistence.ExecuteInsertAsync(insert);

        var product = await _persistence.FindByKeyAsync<PstProduct>(new object[] { 42 });

        Assert.NotNull(product);
        Assert.Equal(42,       product!.Id);
        Assert.Equal("Gadget", product.Name);
        Assert.Equal(1500,     product.Price);
    }

    [Fact]
    public async Task Given_MissingKey_When_FindByKeyAsync_Then_ReturnsNull()
    {
        await CreateProductsTable();

        var result = await _persistence.FindByKeyAsync<PstProduct>(new object[] { 99 });

        Assert.Null(result);
    }

    // ── UPDATE ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_SingleKeyUpdate_When_ExecuteUpdateAsync_Then_UpdatesMatchingRow()
    {
        await CreateProductsTable();

        // Seed two rows
        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[]
            {
                new object?[] { 1, "Old", 100 },
                new object?[] { 2, "Keep", 200 },
            });
        await _persistence.ExecuteInsertAsync(insert);

        // Update only row with Id = 1
        var update = new GroupedUpdate("pst_products", null,
            new[] { new SetClause("Name", "New"), new SetClause("Price", 999) },
            new[] { "Id" },
            new[] { new object?[] { 1 } });

        await _persistence.ExecuteUpdateAsync(update);

        Assert.Equal("New",  await ScalarAsync("SELECT Name  FROM pst_products WHERE Id = 1"));
        Assert.Equal(999L,   await ScalarAsync("SELECT Price FROM pst_products WHERE Id = 1"));
        Assert.Equal("Keep", await ScalarAsync("SELECT Name  FROM pst_products WHERE Id = 2"));
    }

    [Fact]
    public async Task Given_MultipleKeysInClause_When_ExecuteUpdateAsync_Then_UpdatesAllMatchingRows()
    {
        await CreateProductsTable();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[]
            {
                new object?[] { 1, "A", 1 },
                new object?[] { 2, "B", 2 },
                new object?[] { 3, "C", 3 },
            });
        await _persistence.ExecuteInsertAsync(insert);

        // Update rows 1 and 3 (grouped update — same SET clause shape, different keys)
        var update = new GroupedUpdate("pst_products", null,
            new[] { new SetClause("Price", 0) },
            new[] { "Id" },
            new[] { new object?[] { 1 }, new object?[] { 3 } });

        await _persistence.ExecuteUpdateAsync(update);

        Assert.Equal(0L, await ScalarAsync("SELECT Price FROM pst_products WHERE Id = 1"));
        Assert.Equal(2L, await ScalarAsync("SELECT Price FROM pst_products WHERE Id = 2")); // unchanged
        Assert.Equal(0L, await ScalarAsync("SELECT Price FROM pst_products WHERE Id = 3"));
    }

    // ── DELETE ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_SingleKeyDelete_When_ExecuteDeleteAsync_Then_RemovesMatchingRow()
    {
        await CreateProductsTable();

        var insert = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[]
            {
                new object?[] { 1, "A", 1 },
                new object?[] { 2, "B", 2 },
            });
        await _persistence.ExecuteInsertAsync(insert);

        var delete = new BatchDelete("pst_products", null,
            new[] { "Id" },
            new[] { new object?[] { 1 } });

        await _persistence.ExecuteDeleteAsync(delete);

        Assert.Equal(1, await CountAsync("pst_products"));
        Assert.Null(await ScalarAsync("SELECT Id FROM pst_products WHERE Id = 1"));
    }

    [Fact]
    public async Task Given_CompositeKeyDelete_When_ExecuteDeleteAsync_Then_RemovesOnlyMatchingRows()
    {
        await CreateLinesTable();

        var insert = new MultiRowInsert("pst_lines", null,
            new[] { "OrderId", "ItemId", "Qty" },
            new[]
            {
                new object?[] { 1, 10, 5 },
                new object?[] { 1, 20, 3 },
                new object?[] { 2, 10, 7 },
            });
        await _persistence.ExecuteInsertAsync(insert);

        // Delete (OrderId=1, ItemId=10) and (OrderId=2, ItemId=10)
        var delete = new BatchDelete("pst_lines", null,
            new[] { "OrderId", "ItemId" },
            new[]
            {
                new object?[] { 1, 10 },
                new object?[] { 2, 10 },
            });

        await _persistence.ExecuteDeleteAsync(delete);

        Assert.Equal(1, await CountAsync("pst_lines"));
        Assert.Equal(
            1L,
            await ScalarAsync("SELECT COUNT(*) FROM pst_lines WHERE OrderId=1 AND ItemId=20"));
    }

    // ── ExecuteAsync dispatcher ───────────────────────────────────────────────

    [Fact]
    public async Task Given_MultiRowInsert_When_ExecuteAsync_Then_InsertsRows()
    {
        await CreateProductsTable();

        OptimizedOperation op = new MultiRowInsert("pst_products", null,
            new[] { "Id", "Name", "Price" },
            new[] { new object?[] { 7, "X", 77 } });

        await _persistence.ExecuteAsync(op);

        Assert.Equal(1, await CountAsync("pst_products"));
    }
}
