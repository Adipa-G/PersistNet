using System;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: entity with a composite primary key (two key columns).
///
/// <see cref="OrderLine"/> uses <c>(OrderId, LineNumber)</c> as its composite PK.
/// Neither column is AutoIncrement — both values are supplied by the caller.
///
/// INSERT detection heuristic: <see cref="ITransaction.Save"/> treats an entity
/// as INSERT only when ALL key columns are at their default values (0 for int).
/// For INSERT tests, key values of (0, 0) are used; all other tests seed data
/// via direct SQL and exercise GetAsync / Update / Delete through the ORM.
///
/// Schema:
///   sc_order_lines (OrderId INTEGER NOT NULL, LineNumber INTEGER NOT NULL,
///                   ProductName TEXT NOT NULL, Quantity REAL NOT NULL,
///                   PRIMARY KEY (OrderId, LineNumber))
/// </summary>
public sealed class CompositeKeyScenarioTests : ScenarioTestBase
{
    // ── Entity model ─────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_order_lines")]
    private class OrderLine
    {
        [ColumnInfo(Key = true, KeyOrder = 1)]
        public int OrderId { get; set; }

        [ColumnInfo(Key = true, KeyOrder = 2)]
        public int LineNumber { get; set; }

        [ColumnInfo]
        public string ProductName { get; set; } = "";

        [ColumnInfo]
        public decimal Quantity { get; set; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task CreateTableAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_order_lines (" +
            "OrderId INTEGER NOT NULL, " +
            "LineNumber INTEGER NOT NULL, " +
            "ProductName TEXT NOT NULL, " +
            "Quantity REAL NOT NULL, " +
            "PRIMARY KEY (OrderId, LineNumber))");
    }

    private async Task SeedRowAsync(int orderId, int lineNumber, string productName, double qty)
    {
        await ExecAsync(
            $"INSERT INTO sc_order_lines (OrderId, LineNumber, ProductName, Quantity) " +
            $"VALUES ({orderId}, {lineNumber}, '{productName}', {qty})");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save.Insert path: when all key columns are at their default values (0),
    /// the ORM detects an INSERT and writes both PK columns and all data columns.
    /// </summary>
    [Fact]
    public async Task Save_AllDefaultKeys_BothKeyColumnsPersisted()
    {
        await CreateTableAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new OrderLine { OrderId = 0, LineNumber = 0, ProductName = "Widget", Quantity = 3 });
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_order_lines"));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync("SELECT OrderId FROM sc_order_lines")));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync("SELECT LineNumber FROM sc_order_lines")));
        Assert.Equal("Widget", await ScalarAsync("SELECT ProductName FROM sc_order_lines"));
    }

    /// <summary>
    /// GetAsync with both key values must return the correct row with all fields populated.
    /// </summary>
    [Fact]
    public async Task GetAsync_CompositeKey_ReturnsCorrectRow()
    {
        await CreateTableAsync();
        await SeedRowAsync(7, 2, "Gadget", 5.0);
        await SeedRowAsync(7, 3, "Doohickey", 1.0);

        await using var txn = await Factory.OpenTransactionAsync();
        var line = await txn.GetAsync<OrderLine>(7, 2);

        Assert.Equal(7, line.OrderId);
        Assert.Equal(2, line.LineNumber);
        Assert.Equal("Gadget", line.ProductName);
        Assert.Equal(5m, line.Quantity);
    }

    /// <summary>
    /// GetAsync with a key that does not match any row must throw.
    /// </summary>
    [Fact]
    public async Task GetAsync_WrongCompositeKey_Throws()
    {
        await CreateTableAsync();
        await SeedRowAsync(1, 1, "Widget", 1.0);

        await using var txn = await Factory.OpenTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            txn.GetAsync<OrderLine>(1, 99));
    }

    /// <summary>
    /// After a GetAsync, modifying a non-key column and calling Save should issue
    /// an UPDATE that only touches the changed column.
    /// </summary>
    [Fact]
    public async Task Update_DirtyTracking_OnlyChangedColumnUpdated()
    {
        await CreateTableAsync();
        await SeedRowAsync(2, 1, "Original", 10.0);

        await using var txn = await Factory.OpenTransactionAsync();
        var line = await txn.GetAsync<OrderLine>(2, 1);
        line.ProductName = "Updated";
        txn.Save(line);
        await txn.CommitAsync();

        Assert.Equal("Updated", await ScalarAsync(
            "SELECT ProductName FROM sc_order_lines WHERE OrderId = 2 AND LineNumber = 1"));
        // Quantity must be unchanged.
        Assert.Equal(10.0, Convert.ToDouble(await ScalarAsync(
            "SELECT Quantity FROM sc_order_lines WHERE OrderId = 2 AND LineNumber = 1")));
    }

    /// <summary>
    /// Deleting one OrderLine must remove exactly that row and leave others intact.
    /// </summary>
    [Fact]
    public async Task Delete_OneRow_OtherRowsUntouched()
    {
        await CreateTableAsync();
        await SeedRowAsync(3, 1, "A", 1.0);
        await SeedRowAsync(3, 2, "B", 2.0);

        await using var txn = await Factory.OpenTransactionAsync();
        var toDelete = await txn.GetAsync<OrderLine>(3, 1);
        txn.Delete(toDelete);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_order_lines"));
        Assert.Equal("B", await ScalarAsync("SELECT ProductName FROM sc_order_lines"));
    }
}
