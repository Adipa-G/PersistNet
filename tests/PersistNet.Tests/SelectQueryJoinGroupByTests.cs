using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet.DbInfo;
using PersistNet.Query;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Integration tests for the extended fluent query API:
/// INNER JOIN / LEFT JOIN, GroupBy, Having (lambda / Expr aggregate / raw SQL),
/// and raw-SQL escape hatches for Where and OrderBy.
/// Each test class instance owns a fresh in-memory SQLite database.
/// </summary>
public sealed class SelectQueryJoinGroupByTests : IAsyncDisposable
{
    private readonly SqliteConnection   _connection;
    private readonly TransactionFactory _factory;

    // ── Test entities ────────────────────────────────────────────────────

    [TableInfo(TableName = "jg_orders")]
    private class Order
    {
        [ColumnInfo(Key = true)]        public int    Id         { get; set; }
        [ColumnInfo]                    public int    CustomerId { get; set; }
        [ColumnInfo]                    public string Region     { get; set; } = "";
        [ColumnInfo]                    public int    Total      { get; set; }
    }

    [TableInfo(TableName = "jg_customers")]
    private class Customer
    {
        [ColumnInfo(Key = true)]        public int    Id      { get; set; }
        [ColumnInfo]                    public string Name    { get; set; } = "";
        [ColumnInfo(Nullable = true)]   public string? Country { get; set; }
    }

    // ── Setup / teardown ─────────────────────────────────────────────────

    public SelectQueryJoinGroupByTests()
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

    /// <summary>
    /// Schema and seed data:
    ///   Customers: Alice (AU, id=1), Bob (US, id=2)
    ///   Orders: 5 rows — 4 with valid customer IDs, 1 orphan (CustomerId=99)
    ///   Regions: Asia → 4 orders (total=600), Europe → 1 order (total=200)
    /// </summary>
    private async Task SeedAsync()
    {
        // Tables without FK constraints so we can insert an orphan order for LEFT JOIN tests
        await ExecAsync("""
            CREATE TABLE jg_customers (
                Id      INTEGER PRIMARY KEY,
                Name    TEXT    NOT NULL,
                Country TEXT)
            """);

        await ExecAsync("""
            CREATE TABLE jg_orders (
                Id         INTEGER PRIMARY KEY,
                CustomerId INTEGER NOT NULL,
                Region     TEXT    NOT NULL,
                Total      INTEGER NOT NULL)
            """);

        // Customers
        await ExecAsync("INSERT INTO jg_customers VALUES (1, 'Alice', 'AU')");
        await ExecAsync("INSERT INTO jg_customers VALUES (2, 'Bob',   'US')");

        // Orders with valid customer IDs
        await ExecAsync("INSERT INTO jg_orders VALUES (1, 1, 'Asia',   100)");  // Alice, Asia
        await ExecAsync("INSERT INTO jg_orders VALUES (2, 1, 'Europe', 200)");  // Alice, Europe
        await ExecAsync("INSERT INTO jg_orders VALUES (3, 2, 'Asia',   300)");  // Bob,   Asia
        await ExecAsync("INSERT INTO jg_orders VALUES (4, 2, 'Asia',   150)");  // Bob,   Asia

        // Orphan order — CustomerId 99 has no matching Customer row (used for LEFT JOIN test)
        await ExecAsync("INSERT INTO jg_orders VALUES (5, 99, 'Asia',   50)");
    }

    // ── JOIN tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task InnerJoin_ExcludesOrphanRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .ToListAsync();

        // Orphan order (CustomerId=99) has no matching Customer → excluded by INNER JOIN
        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(results, o => o.Id == 5);
    }

    [Fact]
    public async Task LeftJoin_IncludesOrphanRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .ToListAsync();

        // LEFT JOIN keeps all Order rows even when no Customer matches
        Assert.Equal(5, results.Count);
        Assert.Contains(results, o => o.Id == 5);
    }

    [Fact]
    public async Task InnerJoin_WhereOnPrimary_FiltersOrders()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where(o => o.Total > 150)
            .ToListAsync();

        // Orders with Total > 150 and a matching customer: Id=2(200), Id=3(300)
        Assert.Equal(2, results.Count);
        Assert.All(results, o => Assert.True(o.Total > 150));
    }

    [Fact]
    public async Task InnerJoin_WhereOnJoined_Lambda_FiltersCustomers()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where<Customer>(c => c.Country == "AU")
            .ToListAsync();

        // Only Alice (AU) has orders → Id=1 (100) and Id=2 (200)
        Assert.Equal(2, results.Count);
        Assert.All(results, o => Assert.Equal(1, o.CustomerId));
    }

    [Fact]
    public async Task InnerJoin_WhereOnJoined_ExprBuilder_FiltersCustomers()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where(Expr.Field<Customer>(c => c.Country).Eq().Value("US"))
            .ToListAsync();

        // Only Bob (US) orders → Id=3 (300) and Id=4 (150)
        Assert.Equal(2, results.Count);
        Assert.All(results, o => Assert.Equal(2, o.CustomerId));
    }

    [Fact]
    public async Task InnerJoin_CombinedPrimaryAndJoinedFilters()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where(o => o.Region == "Asia")
            .Where<Customer>(c => c.Country == "US")
            .ToListAsync();

        // Bob (US) + Asia only → Id=3 (300) and Id=4 (150)
        Assert.Equal(2, results.Count);
        Assert.All(results, o => Assert.Equal("Asia", o.Region));
        Assert.All(results, o => Assert.Equal(2, o.CustomerId));
    }

    [Fact]
    public async Task InnerJoin_CountAsync_CountsMatchingRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var count = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .CountAsync();

        Assert.Equal(4, count); // 4 orders with a matching customer
    }

    // ── GroupBy + Having tests ────────────────────────────────────────────

    [Fact]
    public async Task GroupBy_Field_ReturnsOneRowPerGroup()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // GROUP BY Region — SQLite returns one row per distinct region
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .ToListAsync();

        // 2 distinct regions: Asia, Europe
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GroupBy_Having_Lambda_FiltersGroups()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(o => o.Region == "Asia")
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_Having_AggregateCount_Expr()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // HAVING COUNT(*) > 2 — only Asia has 4 orders > 2
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Count().Gt().Value(2))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_Having_AggregateSum_Expr()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Asia total = 100 + 300 + 150 + 50 = 600 > 500
        // Europe total = 200 ≤ 500
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Sum<Order>(o => o.Total).Gt().Value(500))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_Having_Aggregate_Neq_Expr()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // HAVING COUNT(*) != 1 → only Asia (count=4 != 1); Europe count=1 excluded
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Count().Neq().Value(1))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_Having_RawSql_FiltersGroups()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Raw SQL HAVING
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having("COUNT(*) > @n", new { n = 2 })
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_RawSql_WithHaving()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // GroupBy using raw SQL string
        var results = await txn.Query<Order>()
            .GroupBy("\"Region\"")
            .Having(Expr.Count().Gt().Value(3))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task GroupBy_Where_Then_Having_CombineFilters()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // WHERE Total >= 100 excludes the orphan (Total=50)
        // GROUP BY Region → Asia: 100+300+150=550, Europe: 200
        // HAVING SUM(Total) > 400 → only Asia
        var results = await txn.Query<Order>()
            .Where(o => o.Total >= 100)
            .GroupBy(o => o.Region)
            .Having(Expr.Sum<Order>(o => o.Total).Gt().Value(400))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    // ── Raw SQL escape-hatch tests ────────────────────────────────────────

    [Fact]
    public async Task Where_RawSql_AnonymousParams_FiltersRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .Where("\"Total\" > @min", new { min = 250 })
            .ToListAsync();

        // Only Id=3 (Total=300) is > 250
        Assert.Single(results);
        Assert.Equal(3, results[0].Id);
    }

    [Fact]
    public async Task Where_RawSql_MultipleParams_FiltersRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .Where("\"Total\" BETWEEN @lo AND @hi", new { lo = 100, hi = 200 })
            .ToListAsync();

        // Total: 100 (Id=1), 150 (Id=4), 200 (Id=2) — all in range
        Assert.Equal(3, results.Count);
        Assert.All(results, o => Assert.InRange(o.Total, 100, 200));
    }

    [Fact]
    public async Task Where_ExprRawSql_FiltersRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .Where(Expr.RawSql("\"Region\" = @r", new { r = "Europe" }))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Europe", results[0].Region);
    }

    [Fact]
    public async Task OrderBy_RawSql_SortsRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .OrderBy("\"Total\" DESC")
            .ToListAsync();

        // Descending: 300, 200, 150, 100, 50
        Assert.Equal(5, results.Count);
        Assert.Equal(300, results[0].Total);
        Assert.Equal(200, results[1].Total);
        Assert.Equal(50,  results[^1].Total);
    }

    [Fact]
    public async Task OrderBy_RawSql_AppendedAfterTypedOrderBy()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Typed OrderBy on Region (ASC) then raw LIMIT via Take, verifying both work
        var results = await txn.Query<Order>()
            .OrderBy(o => o.Region)      // typed: Region ASC
            .OrderBy("\"Total\" DESC")   // raw: Total DESC as secondary
            .ToListAsync();

        Assert.Equal(5, results.Count);
        // All Asia rows first (alphabetically before Europe), sorted by Total DESC within Asia
        var asiaOrders = results.TakeWhile(o => o.Region == "Asia").ToList();
        var europeanOrders = results.SkipWhile(o => o.Region == "Asia").ToList();
        Assert.Equal(4, asiaOrders.Count);
        Assert.Single(europeanOrders);
        // Asia rows should be in descending Total order: 300, 150, 100, 50
        Assert.Equal(300, asiaOrders[0].Total);
    }

    // ── Aggregate expressions without GroupBy ─────────────────────────────

    [Fact]
    public async Task Expr_Min_InHaving_WithGroupBy()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Asia MIN(Total)=50, Europe MIN(Total)=200
        // HAVING MIN(Total) > 100 → only Europe
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Min<Order>(o => o.Total).Gt().Value(100))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Europe", results[0].Region);
    }

    [Fact]
    public async Task Expr_Max_InHaving_WithGroupBy()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Asia MAX(Total)=300, Europe MAX(Total)=200
        // HAVING MAX(Total) >= 300 → only Asia
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Max<Order>(o => o.Total).Ge().Value(300))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Asia", results[0].Region);
    }

    [Fact]
    public async Task Expr_Avg_InHaving_WithGroupBy()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Asia AVG(Total) = (100+300+150+50)/4 = 150, Europe AVG = 200
        // HAVING AVG(Total) > 175 → only Europe
        var results = await txn.Query<Order>()
            .GroupBy(o => o.Region)
            .Having(Expr.Avg<Order>(o => o.Total).Gt().Value(175))
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Europe", results[0].Region);
    }

    // ── Distinct ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Distinct_WithJoin_DeduplicatesPrimaryEntityRows()    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Querying Customer joined with Order inflates Customer rows:
        // Alice appears twice (orders 1, 2), Bob appears twice (orders 3, 4).
        // Without DISTINCT: 4 Customer rows; with DISTINCT: 2 unique Customer rows.
        var withoutDistinct = await txn.Query<Customer>()
            .InnerJoin<Order>((c, o) => c.Id == o.CustomerId)
            .ToListAsync();

        var withDistinct = await txn.Query<Customer>()
            .InnerJoin<Order>((c, o) => c.Id == o.CustomerId)
            .Distinct()
            .ToListAsync();

        Assert.Equal(4, withoutDistinct.Count);
        Assert.Equal(2, withDistinct.Count);
        Assert.Contains(withDistinct, c => c.Name == "Alice");
        Assert.Contains(withDistinct, c => c.Name == "Bob");
    }

    [Fact]
    public async Task Distinct_WithJoinAndFilter_DeduplicatesFilteredRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // AU customers joined with their orders: Alice (AU) has 2 orders → 2 rows without DISTINCT
        // DISTINCT + WHERE collapses them to 1 Customer row
        var results = await txn.Query<Customer>()
            .InnerJoin<Order>((c, o) => c.Id == o.CustomerId)
            .Where<Customer>(c => c.Country == "AU")
            .Distinct()
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal("Alice", results[0].Name);
    }

    // ── Projection (Select<TDto>) ─────────────────────────────────────────

    /// <summary>DTO pulling columns from both joined tables (no name conflicts).</summary>
    private class OrderCustomerSummaryDto
    {
        [ColumnInfo] public string Name   { get; set; } = "";  // from jg_customers
        [ColumnInfo] public string Region { get; set; } = "";  // from jg_orders
        [ColumnInfo] public int    Total  { get; set; }        // from jg_orders
    }

    /// <summary>DTO using [FromTable] to disambiguate both tables' "Id" column.</summary>
    private class OrderCustomerIdsDto
    {
        [ColumnInfo(ColumnName = "CustomerId"), FromTable(typeof(Customer), ColumnName = "Id")]
        public int CustomerId { get; set; }   // Customer.Id → t1."Id" AS "CustomerId"

        [ColumnInfo(ColumnName = "OrderId"), FromTable(typeof(Order), ColumnName = "Id")]
        public int OrderId { get; set; }      // Order.Id   → t0."Id" AS "OrderId"

        [ColumnInfo]
        public int Total { get; set; }
    }

    [Fact]
    public async Task Select_Join_ProjectsColumnsFromBothTables()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // INNER JOIN excludes orphan order (CustomerId=99) → 4 rows
        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select<OrderCustomerSummaryDto>()
            .ToListAsync();

        Assert.Equal(4, results.Count);
        // All rows have a non-empty customer name (proves cross-table column resolution worked)
        Assert.All(results, r => Assert.NotEmpty(r.Name));
        Assert.Contains(results, r => r.Name == "Alice" && r.Total == 100);
        Assert.Contains(results, r => r.Name == "Bob"   && r.Total == 300);
    }

    [Fact]
    public async Task Select_Join_WithFilter_ProjectsFilteredRows()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where<Customer>(c => c.Country == "AU")  // Alice only
            .Select<OrderCustomerSummaryDto>()
            .ToListAsync();

        Assert.Equal(2, results.Count); // Alice's 2 orders
        Assert.All(results, r => Assert.Equal("Alice", r.Name));
    }

    [Fact]
    public async Task Select_Join_WithOrderByAfterProjection_SortsByDtoProperty()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Select<OrderCustomerSummaryDto>()
            .OrderByDescending(dto => dto.Total)
            .ToListAsync();

        Assert.Equal(4, results.Count);
        Assert.Equal(300, results[0].Total); // Bob, Asia, 300 is highest
        Assert.Equal(100, results[3].Total); // Alice, Asia, 100 is lowest
    }

    [Fact]
    public async Task Select_Join_WithFromTable_DisambiguatesConflictingColumnNames()
    {
        await SeedAsync();
        await using var txn = await _factory.OpenTransactionAsync();

        // Both Order.Id and Customer.Id map to DB column "Id".
        // [FromTable] pins each DTO property to the correct table.
        var results = await txn.Query<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where(o => o.Id == 1)  // Order Id=1, CustomerId=1 (Alice), Total=100
            .Select<OrderCustomerIdsDto>()
            .ToListAsync();

        Assert.Single(results);
        Assert.Equal(1,   results[0].OrderId);    // Order.Id
        Assert.Equal(1,   results[0].CustomerId); // Customer.Id (Alice)
        Assert.Equal(100, results[0].Total);
    }
}
