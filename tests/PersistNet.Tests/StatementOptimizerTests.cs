using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;
using PersistNet.Entities;
using PersistNet.Entities.VirtualDb;
using Xunit;

namespace PersistNet.Tests;

public class StatementOptimizerTests
{
    // -------------------------------------------------------------------------
    // Fixture types — registered into DbInfoCache on first use via GetOrExtract.
    // -------------------------------------------------------------------------

    [TableInfo(TableName = "products")]
    private class Product
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ColumnInfo]
        public decimal Price { get; set; }
    }

    [TableInfo(TableName = "order_lines")]
    private class OrderLine
    {
        [ColumnInfo(Key = true)]
        public int OrderId { get; set; }

        [ColumnInfo(Key = true)]
        public int ProductId { get; set; }

        [ColumnInfo]
        public int Quantity { get; set; }
    }

    // Warm the cache for the fixture assembly so FindTableByName works.
    private static void WarmCache() => DbInfoCache.GetOrExtract(typeof(Product).Assembly);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static VRow InsertRow(params (string Col, object? Val)[] cells)
    {
        var row = new VRow(OperationType.Insert);
        foreach (var (col, val) in cells) row.Cells.Add(new VCell(col, val));
        return row;
    }

    private static VRow UpdateRow(params (string Col, object? Val)[] cells)
    {
        var row = new VRow(OperationType.Update);
        foreach (var (col, val) in cells) row.Cells.Add(new VCell(col, val));
        return row;
    }

    private static VRow DeleteRow(params (string Col, object? Val)[] cells)
    {
        var row = new VRow(OperationType.Delete);
        foreach (var (col, val) in cells) row.Cells.Add(new VCell(col, val));
        return row;
    }

    // -------------------------------------------------------------------------
    // Empty input
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_EmptyVTable_When_Optimize_Then_ReturnsEmptyList()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Insert, new List<VRow>());
        var result = StatementOptimizer.Optimize(vtable);
        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // INSERT
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_MultipleInsertRows_When_Optimize_Then_ProducesOneMultiRowInsert()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Insert, new List<VRow>
        {
            InsertRow(("Id", 0), ("Name", "Widget"), ("Price", 9.99m)),
            InsertRow(("Id", 0), ("Name", "Gadget"), ("Price", 19.99m)),
            InsertRow(("Id", 0), ("Name", "Doohickey"), ("Price", 4.99m))
        });

        var result = StatementOptimizer.Optimize(vtable);

        var insert = Assert.Single(result);
        var mri = Assert.IsType<MultiRowInsert>(insert);
        Assert.Equal(3, mri.ValueRows.Count);
        Assert.Equal("products", mri.TableName);
    }

    [Fact]
    public void Given_InsertRowsWithShuffledColumns_When_Optimize_Then_ColumnOrderIsConsistentAcrossAllRows()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Insert, new List<VRow>
        {
            InsertRow(("Price", 1.0m), ("Id", 0), ("Name", "A")),  // shuffled order
            InsertRow(("Name", "B"), ("Price", 2.0m), ("Id", 0))   // different shuffle
        });

        var result = StatementOptimizer.Optimize(vtable);
        var mri = Assert.IsType<MultiRowInsert>(Assert.Single(result));

        // Columns must be alphabetically ordered and identical for both rows.
        Assert.Equal(new[] { "Id", "Name", "Price" }, mri.Columns,
            StringComparer.OrdinalIgnoreCase);

        // Each value row must align to the same column order.
        var idIdx = mri.Columns.ToList().FindIndex(c =>
            string.Equals(c, "Id", StringComparison.OrdinalIgnoreCase));
        var nameIdx = mri.Columns.ToList().FindIndex(c =>
            string.Equals(c, "Name", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, mri.ValueRows[0][idIdx]);
        Assert.Equal("A", mri.ValueRows[0][nameIdx]);
        Assert.Equal(0, mri.ValueRows[1][idIdx]);
        Assert.Equal("B", mri.ValueRows[1][nameIdx]);
    }

    // -------------------------------------------------------------------------
    // DELETE
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_MultipleDeleteRows_When_Optimize_Then_ProducesOneBatchDelete()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Delete, new List<VRow>
        {
            DeleteRow(("Id", 1)),
            DeleteRow(("Id", 2)),
            DeleteRow(("Id", 3))
        });

        var result = StatementOptimizer.Optimize(vtable);

        var delete = Assert.Single(result);
        var bd = Assert.IsType<BatchDelete>(delete);
        Assert.Equal(3, bd.KeyValues.Count);
        Assert.Equal(new[] { "Id" }, bd.KeyColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(bd.KeyValues, kv => Equals(kv[0], 1));
        Assert.Contains(bd.KeyValues, kv => Equals(kv[0], 2));
        Assert.Contains(bd.KeyValues, kv => Equals(kv[0], 3));
    }

    [Fact]
    public void Given_CompositeKeyDeleteRows_When_Optimize_Then_AllColumnsPresent()
    {
        WarmCache();
        var vtable = new VTable("order_lines", null, OperationType.Delete, new List<VRow>
        {
            DeleteRow(("OrderId", 10), ("ProductId", 1)),
            DeleteRow(("OrderId", 10), ("ProductId", 2))
        });

        var result = StatementOptimizer.Optimize(vtable);

        var bd = Assert.IsType<BatchDelete>(Assert.Single(result));
        Assert.Equal(2, bd.KeyValues.Count);
        Assert.Equal(2, bd.KeyColumns.Count); // both composite key columns present
        Assert.Contains("OrderId", bd.KeyColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ProductId", bd.KeyColumns, StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // UPDATE — grouping
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_AllUpdateRowsIdentical_When_Optimize_Then_ProducesOneGroupedUpdate()
    {
        WarmCache();
        // All three rows set the same columns to the same values → one group.
        var vtable = new VTable("products", null, OperationType.Update, new List<VRow>
        {
            UpdateRow(("Id", 1), ("Name", "Widget"), ("Price", 9.99m)),
            UpdateRow(("Id", 2), ("Name", "Widget"), ("Price", 9.99m)),
            UpdateRow(("Id", 3), ("Name", "Widget"), ("Price", 9.99m))
        });

        var result = StatementOptimizer.Optimize(vtable);

        var update = Assert.Single(result);
        var gu = Assert.IsType<GroupedUpdate>(update);
        Assert.Equal(3, gu.KeyValues.Count);
        Assert.Equal(new[] { "Id" }, gu.KeyColumns, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, gu.SetClauses.Count); // Name + Price
    }

    [Fact]
    public void Given_TwoDistinctNonKeyValueSets_When_Optimize_Then_ProducesTwoGroups()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Update, new List<VRow>
        {
            UpdateRow(("Id", 1), ("Name", "Alpha"), ("Price", 1.0m)),
            UpdateRow(("Id", 2), ("Name", "Alpha"), ("Price", 1.0m)),
            UpdateRow(("Id", 3), ("Name", "Beta"),  ("Price", 2.0m)),
            UpdateRow(("Id", 4), ("Name", "Beta"),  ("Price", 2.0m))
        });

        var result = StatementOptimizer.Optimize(vtable);

        Assert.Equal(2, result.Count);
        Assert.All(result, op => Assert.IsType<GroupedUpdate>(op));

        var groups = result.Cast<GroupedUpdate>().ToList();
        Assert.All(groups, g => Assert.Equal(2, g.KeyValues.Count));

        var alphaGroup = groups.Single(g =>
            g.SetClauses.Any(sc => Equals(sc.Value, "Alpha")));
        var betaGroup = groups.Single(g =>
            g.SetClauses.Any(sc => Equals(sc.Value, "Beta")));

        Assert.Contains(alphaGroup.KeyValues, kv => Equals(kv[0], 1));
        Assert.Contains(alphaGroup.KeyValues, kv => Equals(kv[0], 2));
        Assert.Contains(betaGroup.KeyValues, kv => Equals(kv[0], 3));
        Assert.Contains(betaGroup.KeyValues, kv => Equals(kv[0], 4));
    }

    /// <summary>
    /// The "Example 3" scenario from the design discussion:
    /// 10 rows, all with A=10, 5 with Y=15 and 5 with Y=20 → exactly 2 statements.
    /// </summary>
    [Fact]
    public void Given_SharedPropertyAndSplitProperty_When_Optimize_Then_ProducesTwoGroups()
    {
        WarmCache();
        // Re-use Product: map Name → property A, Price → property Y (just different values).
        var rows = new List<VRow>();

        // Group 1: Name="shared", Price=15 (ids 1–5)
        for (var i = 1; i <= 5; i++)
            rows.Add(UpdateRow(("Id", i), ("Name", "shared"), ("Price", 15m)));

        // Group 2: Name="shared", Price=20 (ids 6–10)
        for (var i = 6; i <= 10; i++)
            rows.Add(UpdateRow(("Id", i), ("Name", "shared"), ("Price", 20m)));

        var vtable = new VTable("products", null, OperationType.Update, rows);
        var result = StatementOptimizer.Optimize(vtable);

        Assert.Equal(2, result.Count);
        Assert.All(result, op => Assert.IsType<GroupedUpdate>(op));

        var groups = result.Cast<GroupedUpdate>().ToList();
        Assert.All(groups, g => Assert.Equal(5, g.KeyValues.Count));

        // Both groups share Name="shared" in their SET clause.
        Assert.All(groups, g =>
            Assert.Contains(g.SetClauses, sc =>
                string.Equals(sc.ColumnName, "Name", StringComparison.OrdinalIgnoreCase)
                && Equals(sc.Value, "shared")));

        // Price differs between groups.
        var prices = groups
            .Select(g => g.SetClauses.Single(sc =>
                string.Equals(sc.ColumnName, "Price", StringComparison.OrdinalIgnoreCase)).Value)
            .Cast<decimal>()
            .OrderBy(p => p)
            .ToList();
        Assert.Equal(new[] { 15m, 20m }, prices);
    }

    [Fact]
    public void Given_InsertWithNullCellValue_When_Optimize_Then_ColumnStillPresentWithNullValue()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Insert, new List<VRow>
        {
            InsertRow(("Id", 0), ("Name", null), ("Price", 9.99m)),
            InsertRow(("Id", 0), ("Name", "Widget"), ("Price", 5.00m))
        });

        var result = StatementOptimizer.Optimize(vtable);
        var mri = Assert.IsType<MultiRowInsert>(Assert.Single(result));

        // The Name column must still appear in the column list.
        Assert.Contains("Name", mri.Columns, StringComparer.OrdinalIgnoreCase);

        // The first row's Name value must be null, not absent.
        var nameIdx = mri.Columns.ToList()
            .FindIndex(c => string.Equals(c, "Name", StringComparison.OrdinalIgnoreCase));
        Assert.Null(mri.ValueRows[0][nameIdx]);

        // The second row's Name value must be the actual string.
        Assert.Equal("Widget", mri.ValueRows[1][nameIdx]);
    }

    [Fact]
    public void Given_UpdateWithNullValueInOneGroup_When_Optimize_Then_DoesNotCollapseWithNonNull()
    {
        WarmCache();
        var vtable = new VTable("products", null, OperationType.Update, new List<VRow>
        {
            UpdateRow(("Id", 1), ("Name", null),    ("Price", 5m)),
            UpdateRow(("Id", 2), ("Name", "Widget"), ("Price", 5m))
        });

        var result = StatementOptimizer.Optimize(vtable);

        // null and "Widget" must not collapse into the same group.
        Assert.Equal(2, result.Count);
    }

    // -------------------------------------------------------------------------
    // Version column (optimistic concurrency)
    // -------------------------------------------------------------------------

    [TableInfo(TableName = "ver_products")]
    private class VersionedProduct
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }
    }

    private static VRow VersionedUpdateRow(int id, string name, long version)
    {
        var row = new VRow(OperationType.Update);
        row.Cells.Add(new VCell("Id", id));
        row.Cells.Add(new VCell("Name", name));
        row.Cells.Add(new VCell("Version", version) { IsVersion = true });
        return row;
    }

    [Fact]
    public void Given_VersionedUpdate_When_Optimize_Then_SetClauseHasIncrementedVersion()
    {
        WarmCache();
        var vtable = new VTable("ver_products", null, OperationType.Update, new List<VRow>
        {
            VersionedUpdateRow(id: 1, name: "Widget", version: 3L)
        });

        var result = StatementOptimizer.Optimize(vtable);

        var gu = Assert.IsType<GroupedUpdate>(Assert.Single(result));
        var versionSet = Assert.Single(gu.SetClauses, sc =>
            string.Equals(sc.ColumnName, "Version", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4L, versionSet.Value); // 3 + 1
    }

    [Fact]
    public void Given_VersionedUpdate_When_Optimize_Then_ExpectedVersionValueIsOriginal()
    {
        WarmCache();
        var vtable = new VTable("ver_products", null, OperationType.Update, new List<VRow>
        {
            VersionedUpdateRow(id: 1, name: "Widget", version: 5L)
        });

        var result = StatementOptimizer.Optimize(vtable);

        var gu = Assert.IsType<GroupedUpdate>(Assert.Single(result));
        Assert.Equal("Version", gu.VersionColumn, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5L, gu.ExpectedVersionValue);
    }

    [Fact]
    public void Given_MultipleRowsSameVersion_When_Optimize_Then_GroupedIntoOneUpdate()
    {
        WarmCache();
        var vtable = new VTable("ver_products", null, OperationType.Update, new List<VRow>
        {
            VersionedUpdateRow(id: 1, name: "A", version: 2L),
            VersionedUpdateRow(id: 2, name: "A", version: 2L) // same name & version → same group
        });

        var result = StatementOptimizer.Optimize(vtable);

        var gu = Assert.IsType<GroupedUpdate>(Assert.Single(result));
        Assert.Equal(2, gu.KeyValues.Count);
        Assert.Equal(3L, gu.SetClauses.Single(sc =>
            string.Equals(sc.ColumnName, "Version", StringComparison.OrdinalIgnoreCase)).Value);
    }

    [Fact]
    public void Given_RowsWithDifferentVersions_When_Optimize_Then_SeparateGroupsPerVersion()
    {
        WarmCache();
        var vtable = new VTable("ver_products", null, OperationType.Update, new List<VRow>
        {
            VersionedUpdateRow(id: 1, name: "X", version: 1L),
            VersionedUpdateRow(id: 2, name: "X", version: 2L) // same name, different version → different group
        });

        var result = StatementOptimizer.Optimize(vtable);

        // Different expected-version values force separate SQL statements.
        Assert.Equal(2, result.Count);
        Assert.All(result, op => Assert.IsType<GroupedUpdate>(op));

        var groups = result.Cast<GroupedUpdate>().ToList();
        Assert.Equal(1L, groups[0].ExpectedVersionValue);
        Assert.Equal(2L, groups[1].ExpectedVersionValue);
    }
}
