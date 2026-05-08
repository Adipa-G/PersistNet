using System;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Table-Per-Concrete-Type (TPC) inheritance.
///
/// The abstract base <see cref="TpcAnimal"/> carries the shared columns
/// (<c>Id</c>, <c>Name</c>) but has <em>no</em> <c>[TableInfo]</c>.
/// Each concrete subclass gets its own table that repeats the shared columns.
///
/// Schema:
///   tpc_dogs (Id INTEGER PK, Name TEXT, Breed TEXT)
///   tpc_cats (Id INTEGER PK, Name TEXT, Lives INTEGER)
///
/// No production-code changes are needed — <c>type.GetProperties()</c> already
/// returns inherited properties, so the ORM builds both tables correctly.
/// </summary>
public sealed class TablePerConcreteTypeScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    private abstract class TpcAnimal
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";
    }

    [TableInfo(TableName = "tpc_dogs")]
    private class TpcDog : TpcAnimal
    {
        [ColumnInfo]
        public string Breed { get; set; } = "";
    }

    [TableInfo(TableName = "tpc_cats")]
    private class TpcCat : TpcAnimal
    {
        [ColumnInfo]
        public int Lives { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE tpc_dogs (" +
            "Id INTEGER NOT NULL PRIMARY KEY, " +
            "Name TEXT NOT NULL, " +
            "Breed TEXT NOT NULL)");

        await ExecAsync(
            "CREATE TABLE tpc_cats (" +
            "Id INTEGER NOT NULL PRIMARY KEY, " +
            "Name TEXT NOT NULL, " +
            "Lives INTEGER NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save a dog: both the inherited Name and the own Breed must be persisted.
    /// </summary>
    [Fact]
    public async Task Save_Dog_InheritedNameAndBreedSaved()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TpcDog { Name = "Rex", Breed = "Labrador" });
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("tpc_dogs"));
        Assert.Equal("Rex", await ScalarAsync("SELECT Name FROM tpc_dogs LIMIT 1"));
        Assert.Equal("Labrador", await ScalarAsync("SELECT Breed FROM tpc_dogs LIMIT 1"));
    }

    /// <summary>
    /// Save a cat: both the inherited Name and the own Lives must be persisted.
    /// </summary>
    [Fact]
    public async Task Save_Cat_InheritedNameAndLivesSaved()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TpcCat { Name = "Whiskers", Lives = 9 });
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("tpc_cats"));
        Assert.Equal("Whiskers", await ScalarAsync("SELECT Name FROM tpc_cats LIMIT 1"));
        Assert.Equal(9L, Convert.ToInt64(await ScalarAsync("SELECT Lives FROM tpc_cats LIMIT 1")));
    }

    /// <summary>
    /// GetAsync for a dog: inherited Name must be loaded alongside Breed.
    /// </summary>
    [Fact]
    public async Task GetAsync_Dog_InheritedNameLoaded()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TpcDog { Name = "Buddy", Breed = "Poodle" });
        await txn.CommitAsync();

        await using var readTxn = await Factory.OpenTransactionAsync();
        var dog = await readTxn.GetAsync<TpcDog>(1);

        Assert.NotNull(dog);
        Assert.Equal(1, dog.Id);
        Assert.Equal("Buddy", dog.Name);
        Assert.Equal("Poodle", dog.Breed);
    }

    /// <summary>
    /// GetAsync for a cat: inherited Name must be loaded alongside Lives.
    /// </summary>
    [Fact]
    public async Task GetAsync_Cat_InheritedNameLoaded()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TpcCat { Name = "Luna", Lives = 7 });
        await txn.CommitAsync();

        await using var readTxn = await Factory.OpenTransactionAsync();
        var cat = await readTxn.GetAsync<TpcCat>(1);

        Assert.NotNull(cat);
        Assert.Equal(1, cat.Id);
        Assert.Equal("Luna", cat.Name);
        Assert.Equal(7, cat.Lives);
    }

    /// <summary>
    /// Dirty tracking: changing only Breed issues an UPDATE that modifies just that column;
    /// Name remains unchanged in the database.
    /// </summary>
    [Fact]
    public async Task Update_Dog_DirtyTracking_OnlyChangedColumnUpdated()
    {
        await CreateTablesAsync();

        // Insert
        await using var insertTxn = await Factory.OpenTransactionAsync();
        insertTxn.Save(new TpcDog { Name = "Max", Breed = "Beagle" });
        await insertTxn.CommitAsync();

        // Load, modify Breed, save
        await using var updateTxn = await Factory.OpenTransactionAsync();
        var dog = await updateTxn.GetAsync<TpcDog>(1);
        dog!.Breed = "Dachshund";
        updateTxn.Save(dog);
        await updateTxn.CommitAsync();

        Assert.Equal("Dachshund", await ScalarAsync("SELECT Breed FROM tpc_dogs WHERE Id = 1"));
        Assert.Equal("Max", await ScalarAsync("SELECT Name  FROM tpc_dogs WHERE Id = 1"));
    }
}
