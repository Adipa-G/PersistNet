using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Root entity (Catalog) with a related entity (Item) that uses a
/// Single-Table Inheritance hierarchy — persisted in a DIFFERENT table.
///
/// Schema:
///   sc_rinh_catalogs  (Id, Name)                         ← root table
///   sc_rinh_items     (Id, CatalogId FK, Title,           ← related table with STI
///                      ItemType TEXT discriminator,
///                      Version INTEGER concurrency token,
///                      Url TEXT nullable,                 -- DigitalItem only
///                      WeightKg REAL nullable)            -- PhysicalItem only
///
/// Subtypes: RinhDigitalItem ("digital"), RinhPhysicalItem ("physical")
///
/// This combined scenario exercises:
///   - One-to-Many from root entity to a polymorphic item hierarchy
///   - Many-to-One FK back-reference on the base class
///   - STI discriminator column written per subtype
///   - Optimistic concurrency (IsVersion) on the base class
///   - Dirty tracking including subtype extra columns
/// </summary>
public sealed class RelatedWithInheritanceScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_rinh_catalogs")]
    private class RinhCatalog
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [OneToManyRelationshipInfo(RelatedType = typeof(RinhItem), MappedBy = "Catalog")]
        public List<RinhItem>? Items { get; set; }
    }

    [TableInfo(TableName = "sc_rinh_items")]
    [SubTypeInfo(typeof(RinhDigitalItem),  "digital")]
    [SubTypeInfo(typeof(RinhPhysicalItem), "physical")]
    private class RinhItem
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int CatalogId { get; set; }

        [ColumnInfo]
        public string Title { get; set; } = "";

        [ColumnInfo(IsDiscriminator = true)]
        public string ItemType { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(RinhCatalog),
            FromKeys = new[] { "CatalogId" },
            ToKeys = new[] { "Id" })]
        public RinhCatalog? Catalog { get; set; }
    }

    private class RinhDigitalItem : RinhItem
    {
        [ColumnInfo]
        public string Url { get; set; } = "";
    }

    private class RinhPhysicalItem : RinhItem
    {
        [ColumnInfo]
        public double WeightKg { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_rinh_catalogs " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE sc_rinh_items (" +
            "Id INTEGER NOT NULL PRIMARY KEY, " +
            "CatalogId INTEGER NOT NULL, " +
            "Title TEXT NOT NULL, " +
            "ItemType TEXT NOT NULL, " +
            "Version INTEGER NOT NULL, " +
            "Url TEXT, " +           // nullable — DigitalItem only
            "WeightKg REAL)");       // nullable — PhysicalItem only
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save a catalog with a digital item AND a physical item.
    /// All three rows must be in their respective tables with correct discriminators.
    /// The catalog row must be persisted before the items (FK dependency).
    /// </summary>
    [Fact]
    public async Task Save_CatalogWithDigitalAndPhysicalItems_AllRowsPersisted()
    {
        await CreateTablesAsync();

        var digital  = new RinhDigitalItem  { CatalogId = 1, Title = "E-Book",   Version = 1, Url = "https://book.zip" };
        var physical = new RinhPhysicalItem { CatalogId = 1, Title = "Paperback", Version = 1, WeightKg = 0.4 };
        var catalog  = new RinhCatalog
        {
            Name = "Tech Store",
            Items = new List<RinhItem> { digital, physical }
        };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(catalog);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_rinh_catalogs"));
        Assert.Equal(2L, await CountAsync("sc_rinh_items"));

        Assert.Equal("digital",  await ScalarAsync("SELECT ItemType FROM sc_rinh_items WHERE Title = 'E-Book'"));
        Assert.Equal("physical", await ScalarAsync("SELECT ItemType FROM sc_rinh_items WHERE Title = 'Paperback'"));

        Assert.Equal("https://book.zip",
            await ScalarAsync("SELECT Url FROM sc_rinh_items WHERE Title = 'E-Book'"));
        Assert.Null(await ScalarAsync("SELECT Url FROM sc_rinh_items WHERE Title = 'Paperback'"));

        Assert.Equal(0.4,
            await ScalarAsync("SELECT WeightKg FROM sc_rinh_items WHERE Title = 'Paperback'"));
        Assert.Null(await ScalarAsync("SELECT WeightKg FROM sc_rinh_items WHERE Title = 'E-Book'"));
    }

    /// <summary>
    /// GetAsync with the concrete digital subtype returns all base and extra columns.
    /// </summary>
    [Fact]
    public async Task GetAsync_DigitalItem_BaseColumnsAndUrlLoaded()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_rinh_catalogs VALUES (1, 'Downloads')");
        await ExecAsync(
            "INSERT INTO sc_rinh_items VALUES (1, 1, 'Tutorial PDF', 'digital', 1, 'https://tut.pdf', NULL)");

        await using var txn = await Factory.OpenTransactionAsync();
        var item = await txn.GetAsync<RinhDigitalItem>(1);

        Assert.Equal(1,                  item.Id);
        Assert.Equal(1,                  item.CatalogId);
        Assert.Equal("Tutorial PDF",     item.Title);
        Assert.Equal("digital",          item.ItemType);
        Assert.Equal(1L,                 item.Version);
        Assert.Equal("https://tut.pdf",  item.Url);
    }

    /// <summary>
    /// GetAsync with the concrete physical subtype returns all base and extra columns.
    /// </summary>
    [Fact]
    public async Task GetAsync_PhysicalItem_BaseColumnsAndWeightLoaded()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_rinh_catalogs VALUES (1, 'Books')");
        await ExecAsync(
            "INSERT INTO sc_rinh_items VALUES (2, 1, 'C# in Depth', 'physical', 1, NULL, 0.7)");

        await using var txn = await Factory.OpenTransactionAsync();
        var item = await txn.GetAsync<RinhPhysicalItem>(2);

        Assert.Equal(2,              item.Id);
        Assert.Equal("C# in Depth", item.Title);
        Assert.Equal("physical",    item.ItemType);
        Assert.Equal(1L,            item.Version);
        Assert.Equal(0.7,           item.WeightKg);
    }

    /// <summary>
    /// KEY DIRTY-TRACKING + STI TEST:
    /// After GetAsync, only the Url column is changed on a RinhDigitalItem.
    /// The UPDATE must:
    ///   1. Bump the Version from 1 → 2  (proves an UPDATE was emitted)
    ///   2. Write the new Url value       (proves the changed column was included)
    ///   3. Leave Title unchanged in the DB (proves dirty tracking excluded unchanged columns)
    /// </summary>
    [Fact]
    public async Task Update_DigitalItem_OnlyChangedUrlUpdated_DirtyTrackingProved()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_rinh_catalogs VALUES (1, 'Tech')");
        await ExecAsync(
            "INSERT INTO sc_rinh_items VALUES (3, 1, 'Old Title', 'digital', 1, 'https://old.zip', NULL)");

        await using var txn = await Factory.OpenTransactionAsync();
        var item = await txn.GetAsync<RinhDigitalItem>(3); // snapshot captured here
        item.Url = "https://new.zip";                     // only Url changed
        txn.Save(item);
        await txn.CommitAsync();

        // Version bumped: confirms UPDATE was emitted (not a no-op).
        Assert.Equal(2L, Convert.ToInt64(
            await ScalarAsync("SELECT Version FROM sc_rinh_items WHERE Id = 3")));

        // Url updated.
        Assert.Equal("https://new.zip",
            await ScalarAsync("SELECT Url FROM sc_rinh_items WHERE Id = 3"));

        // Title unchanged: dirty tracking excluded it from the SET clause.
        Assert.Equal("Old Title",
            await ScalarAsync("SELECT Title FROM sc_rinh_items WHERE Id = 3"));
    }

    /// <summary>
    /// Saving a catalog item from the Many-to-One side (item holds the FK,
    /// Catalog navigation is populated). Catalog is inserted first.
    /// </summary>
    [Fact]
    public async Task Save_PhysicalItemWithCatalogRef_CatalogInsertedFirst()
    {
        await CreateTablesAsync();

        var catalog = new RinhCatalog { Name = "Retail" };
        var item    = new RinhPhysicalItem
        {
            CatalogId = 1, Title = "Notebook",  // CatalogId = 1: first auto-increment for catalog
            Version = 1, WeightKg = 0.3,
            Catalog = catalog   // M2O traversal saves catalog first
        };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(item);   // catalog must be saved first despite saving from item side
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_rinh_catalogs"));
        Assert.Equal(1L, await CountAsync("sc_rinh_items"));
        Assert.Equal("Retail",
            await ScalarAsync("SELECT Name FROM sc_rinh_catalogs LIMIT 1"));
        Assert.Equal(1L, Convert.ToInt64(
            await ScalarAsync("SELECT CatalogId FROM sc_rinh_items LIMIT 1")));
    }

    /// <summary>
    /// Delete a catalog with mixed-type items in the Items list.
    /// Items are deleted first (O2M cascade), then the catalog row.
    /// </summary>
    [Fact]
    public async Task Delete_CatalogWithMixedItems_AllItemsDeletedFirst()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_rinh_catalogs VALUES (1, 'Mixed')");
        await ExecAsync("INSERT INTO sc_rinh_items VALUES (1, 1, 'Digital A', 'digital',  1, 'https://a.zip', NULL)");
        await ExecAsync("INSERT INTO sc_rinh_items VALUES (2, 1, 'Physical B', 'physical', 1, NULL, 1.2)");

        var d1 = new RinhDigitalItem  { Id = 1, CatalogId = 1 };
        var p1 = new RinhPhysicalItem { Id = 2, CatalogId = 1 };
        var catalog = new RinhCatalog { Id = 1, Items = new List<RinhItem> { d1, p1 } };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Delete(catalog);
        await txn.CommitAsync();

        Assert.Equal(0L, await CountAsync("sc_rinh_items"));
        Assert.Equal(0L, await CountAsync("sc_rinh_catalogs"));
    }
}
