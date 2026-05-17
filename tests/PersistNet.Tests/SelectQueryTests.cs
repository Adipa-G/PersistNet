using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet.DbInfo;
using PersistNet.Query;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Integration tests for the fluent <see cref="ISelectQuery{T}"/> API exposed via
/// <see cref="ITransaction.Query{T}()"/>. Each test uses a fresh in-memory SQLite
/// database.
/// </summary>
public sealed class SelectQueryTests : IAsyncDisposable
{
    private readonly SqliteConnection   _connection;
    private readonly TransactionFactory _factory;

    // ── Fixture entity ────────────────────────────────────────────────────

    [TableInfo(TableName = "sq_products")]
    private class Product
    {
        [ColumnInfo(Key = true)]            public int     Id       { get; set; }
        [ColumnInfo]                        public string  Name     { get; set; } = "";
        [ColumnInfo]                        public int     Price    { get; set; }
        [ColumnInfo]                        public bool    IsActive { get; set; }
        [ColumnInfo(Nullable = true)]       public string? Category { get; set; }
    }

    // ── Setup / teardown ─────────────────────────────────────────────────

    public SelectQueryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TransactionFactory(_connection, DbProvider.SQLite,
            NullLogger<TransactionFactory>.Instance);
    }

    public async ValueTask DisposeAsync()
        => await _connection.DisposeAsync();

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task ExecAsync(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync()
    {
        await ExecAsync("""
            CREATE TABLE sq_products (
                Id       INTEGER PRIMARY KEY,
                Name     TEXT    NOT NULL,
                Price    INTEGER NOT NULL,
                IsActive INTEGER NOT NULL,
                Category TEXT)
            """);

        await ExecAsync("INSERT INTO sq_products VALUES (1, 'Apple',  30, 1, 'Fruit')");
        await ExecAsync("INSERT INTO sq_products VALUES (2, 'Banana', 20, 1, 'Fruit')");
        await ExecAsync("INSERT INTO sq_products VALUES (3, 'Carrot', 15, 0, 'Veg')");
        await ExecAsync("INSERT INTO sq_products VALUES (4, 'Daikon', 10, 0, NULL)");
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_NoFilter_ToListAsync_ReturnsAllRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>().ToListAsync();

        Assert.Equal(4, results.Count);
    }

    [Fact]
    public async Task Query_Where_Lambda_Eq_FiltersRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.Name == "Apple")
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal(1, results[0].Id);
    }

    [Fact]
    public async Task Query_Where_Lambda_Gt_FiltersRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.Price > 15)
            .ToListAsync();

        Assert.Equal(2, results.Count); // 30 and 20
    }

    [Fact]
    public async Task Query_Where_Lambda_And_WithinSinglePredicate()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Both conditions in one lambda → p.Price > 10 AND p.IsActive == true
        var results = await txn.Query<Product>()
            .Where(p => p.Price > 10 && p.IsActive)
            .ToListAsync();

        Assert.Equal(2, results.Count); // Apple (30,active) and Banana (20,active)
    }

    [Fact]
    public async Task Query_ChainedWhere_AreAnded()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.IsActive)
            .Where(p => p.Price >= 25)
            .ToListAsync();

        Assert.Single(results); // only Apple (30, active)
        Assert.Equal("Apple", results[0].Name);
    }

    [Fact]
    public async Task Query_Where_StringContains_ProducesLike()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.Name.Contains("an"))
            .ToListAsync();

        // Banana (contains "an")
        Assert.Single(results);
        Assert.Equal("Banana", results[0].Name);
    }

    [Fact]
    public async Task Query_Where_ExprBuilder_Between()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(Expr.Field<Product>(p => p.Price).Between().Values(15, 25))
            .ToListAsync();

        Assert.Equal(2, results.Count); // Banana (20) and Carrot (15)
    }

    [Fact]
    public async Task Query_Where_ExprBuilder_In()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(Expr.Field<Product>(p => p.Id).In().Values(1, 3))
            .ToListAsync();

        Assert.Equal(2, results.Count);
        var ids = new[] { results[0].Id, results[1].Id };
        Assert.Contains(1, ids);
        Assert.Contains(3, ids);
    }

    [Fact]
    public async Task Query_OrderBy_Ascending()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .OrderBy(p => p.Price)
            .ToListAsync();

        Assert.Equal([10, 15, 20, 30], results.Select(r => r.Price));
    }

    [Fact]
    public async Task Query_OrderByDescending()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .OrderByDescending(p => p.Price)
            .ToListAsync();

        Assert.Equal([30, 20, 15, 10], results.Select(r => r.Price));
    }

    [Fact]
    public async Task Query_ThenBy_SecondarySort()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .OrderBy(p => p.IsActive)
            .ThenByDescending(p => p.Price)
            .ToListAsync();

        // IsActive=0 first: Carrot(15), Daikon(10); then IsActive=1: Apple(30), Banana(20)
        Assert.Equal("Carrot", results[0].Name);
        Assert.Equal("Daikon", results[1].Name);
        Assert.Equal("Apple",  results[2].Name);
        Assert.Equal("Banana", results[3].Name);
    }

    [Fact]
    public async Task Query_Take_LimitsResults()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .OrderBy(p => p.Id)
            .Take(2)
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].Id);
        Assert.Equal(2, results[1].Id);
    }

    [Fact]
    public async Task Query_Skip_OffsetResults()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .OrderBy(p => p.Id)
            .Skip(2)
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(3, results[0].Id);
        Assert.Equal(4, results[1].Id);
    }

    [Fact]
    public async Task Query_CountAsync_ReturnsMatchingCount()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var count = await txn.Query<Product>()
            .Where(p => p.IsActive)
            .CountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Query_AnyAsync_TrueWhenRowsMatch()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var any = await txn.Query<Product>()
            .Where(p => p.Price > 25)
            .AnyAsync();

        Assert.True(any);
    }

    [Fact]
    public async Task Query_AnyAsync_FalseWhenNoRowsMatch()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var any = await txn.Query<Product>()
            .Where(p => p.Price > 1000)
            .AnyAsync();

        Assert.False(any);
    }

    [Fact]
    public async Task Query_AnyAsync_WithPredicate()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var any = await txn.Query<Product>().AnyAsync(p => p.Name == "Banana");

        Assert.True(any);
    }

    [Fact]
    public async Task Query_SumAsync_ReturnsTotal()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var sum = await txn.Query<Product>()
            .Where(p => p.IsActive)
            .SumAsync(p => p.Price);

        Assert.Equal(50, sum); // 30 + 20
    }

    [Fact]
    public async Task Query_FirstOrDefaultAsync_ReturnsFirst()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var result = await txn.Query<Product>()
            .OrderBy(p => p.Price)
            .FirstOrDefaultAsync();

        Assert.NotNull(result);
        Assert.Equal("Daikon", result!.Name);
    }

    [Fact]
    public async Task Query_FirstOrDefaultAsync_ReturnsNullWhenNoMatch()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var result = await txn.Query<Product>()
            .Where(p => p.Price > 9999)
            .FirstOrDefaultAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task Query_Where_ClosureCapturedVariable()
    {
        await SeedAsync();
        var minPrice = 20;  // closure variable captured at query-build time

        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.Price >= minPrice)
            .ToListAsync();

        Assert.Equal(2, results.Count); // Apple (30) and Banana (20)
    }

    // ── AverageAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task Query_AverageAsync_ReturnsCorrectAverage()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Prices: 30, 20, 15, 10 → avg = 18.75
        var avg = await txn.Query<Product>().AverageAsync(p => p.Price);

        Assert.NotNull(avg);
        Assert.Equal(18.75, avg!.Value, precision: 5);
    }

    [Fact]
    public async Task Query_AverageAsync_WithFilter_ReturnsFilteredAverage()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Active products only: Apple (30) and Banana (20) → avg = 25
        var avg = await txn.Query<Product>()
            .Where(p => p.IsActive)
            .AverageAsync(p => p.Price);

        Assert.NotNull(avg);
        Assert.Equal(25.0, avg!.Value, precision: 5);
    }

    [Fact]
    public async Task Query_AverageAsync_NoRows_ReturnsNull()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var avg = await txn.Query<Product>()
            .Where(p => p.Price > 9999)
            .AverageAsync(p => p.Price);

        Assert.Null(avg);
    }

    // ── Distinct ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_Distinct_WithNoFilter_EmitsSqlKeyword()
    {
        // Distinct on a single-table query with all unique rows is a pass-through —
        // the important thing is verifying it compiles and executes without error.
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>().Distinct().ToListAsync();

        Assert.Equal(4, results.Count); // all 4 rows are already unique
    }

    // ── Projection (Select<TDto>) ───────────────────────────────────────────

    private class ProductSummaryDto
    {
        [ColumnInfo] public string Name  { get; set; } = "";
        [ColumnInfo] public int    Price { get; set; }
    }

    [Fact]
    public async Task Select_ProjectsSubsetOfColumns()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Project the 5-column Product entity down to 2 columns.
        var results = await txn.Query<Product>()
            .Select<ProductSummaryDto>()
            .ToListAsync();

        Assert.Equal(4, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Name);
            Assert.True(r.Price >= 0);
        });
        Assert.Contains(results, r => r.Name == "Apple"  && r.Price == 30);
        Assert.Contains(results, r => r.Name == "Carrot" && r.Price == 15);
    }

    [Fact]
    public async Task Select_WithFilter_ProjectsFilteredRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Product>()
            .Where(p => p.IsActive)
            .Select<ProductSummaryDto>()
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Price >= 20));
    }

    [Fact]
    public async Task Select_FirstOrDefaultAsync_WithProjection()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var result = await txn.Query<Product>()
            .Where(p => p.Name == "Banana")
            .Select<ProductSummaryDto>()
            .FirstOrDefaultAsync();

        Assert.NotNull(result);
        Assert.Equal("Banana", result!.Name);
        Assert.Equal(20, result.Price);
    }

    [Fact]
    public async Task Select_WithOrderByAfterProjection_SortsByDtoProperty()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // OrderBy on the projected DTO type — resolved via DTO column resolution.
        var results = await txn.Query<Product>()
            .Select<ProductSummaryDto>()
            .OrderByDescending(dto => dto.Price)
            .Take(2)
            .ToListAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal("Apple",  results[0].Name); // Price 30
        Assert.Equal("Banana", results[1].Name); // Price 20
    }

    [Fact]
    public async Task Select_WithOrderByBeforeProjection_SortsByEntityProperty()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // OrderBy on ISelectQuery<T> before Select<TDto>() — resolves to the source table column.
        var results = await txn.Query<Product>()
            .OrderBy(p => p.Price)
            .Select<ProductSummaryDto>()
            .ToListAsync();

        Assert.Equal(4, results.Count);
        Assert.Equal("Daikon", results[0].Name); // Price 10 (cheapest first)
        Assert.Equal("Apple",  results[3].Name); // Price 30 (most expensive last)
    }
}
