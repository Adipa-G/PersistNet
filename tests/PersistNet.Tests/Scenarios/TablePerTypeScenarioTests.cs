using System;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Table-Per-Type (TPT) inheritance.
///
/// The base class <see cref="TptAnimal"/> has <c>[TableInfo]</c> and its own table.
/// Subclasses (<see cref="TptDog"/>, <see cref="TptCat"/>) also have <c>[TableInfo]</c>;
/// the ORM infers TPT from the fact that the direct <c>BaseType</c> also carries
/// <c>[TableInfo]</c> — no extra attribute required.
///
/// Schema:
///   tpt_animals (Id INTEGER PK AUTO, Name TEXT)
///   tpt_dogs    (Id INTEGER PK REFERENCES tpt_animals, Breed TEXT)
///   tpt_cats    (Id INTEGER PK REFERENCES tpt_animals, Lives INTEGER)
///
/// Each subclass INSERT produces two rows: one in <c>tpt_animals</c> and one in
/// the subtype join table.  SELECT uses a JOIN to hydrate all columns in one trip.
/// </summary>
public sealed class TablePerTypeScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "tpt_animals")]
    private class TptAnimal
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";
    }

    // BaseType (TptAnimal) also has [TableInfo] → ORM infers TPT automatically.
    [TableInfo(TableName = "tpt_dogs")]
    private class TptDog : TptAnimal
    {
        [ColumnInfo]
        public string Breed { get; set; } = "";
    }

    [TableInfo(TableName = "tpt_cats")]
    private class TptCat : TptAnimal
    {
        [ColumnInfo]
        public int Lives { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE tpt_animals (" +
            "Id INTEGER NOT NULL PRIMARY KEY, " +
            "Name TEXT NOT NULL)");

        await ExecAsync(
            "CREATE TABLE tpt_dogs (" +
            "Id INTEGER NOT NULL PRIMARY KEY REFERENCES tpt_animals(Id), " +
            "Breed TEXT NOT NULL)");

        await ExecAsync(
            "CREATE TABLE tpt_cats (" +
            "Id INTEGER NOT NULL PRIMARY KEY REFERENCES tpt_animals(Id), " +
            "Lives INTEGER NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saving a dog must produce exactly one row in <c>tpt_animals</c> and one
    /// row in <c>tpt_dogs</c>, with the same shared Id.
    /// </summary>
    [Fact]
    public async Task Save_Dog_RowInBaseTableAndJoinTable()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TptDog { Name = "Rex", Breed = "Labrador" });
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("tpt_animals"));
        Assert.Equal(1L, await CountAsync("tpt_dogs"));
        Assert.Equal(0L, await CountAsync("tpt_cats"));

        // The Id written to tpt_dogs must match the auto-generated Id in tpt_animals.
        var animalId = Convert.ToInt64(await ScalarAsync("SELECT Id FROM tpt_animals LIMIT 1"));
        var dogId    = Convert.ToInt64(await ScalarAsync("SELECT Id FROM tpt_dogs    LIMIT 1"));
        Assert.Equal(animalId, dogId);

        Assert.Equal("Rex",     await ScalarAsync("SELECT Name  FROM tpt_animals LIMIT 1"));
        Assert.Equal("Labrador", await ScalarAsync("SELECT Breed FROM tpt_dogs    LIMIT 1"));
    }

    /// <summary>
    /// Saving a dog and a cat: each type goes into its own join table; the shared
    /// base table receives both rows.
    /// </summary>
    [Fact]
    public async Task Save_Mixed_EachTableGetsCorrectRows()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TptDog { Name = "Buddy", Breed = "Poodle" });
        txn.Save(new TptCat { Name = "Whiskers", Lives = 9 });
        await txn.CommitAsync();

        Assert.Equal(2L, await CountAsync("tpt_animals"));
        Assert.Equal(1L, await CountAsync("tpt_dogs"));
        Assert.Equal(1L, await CountAsync("tpt_cats"));
    }

    /// <summary>
    /// GetAsync for a dog: both the base-table Name and the join-table Breed must
    /// be loaded into a single <see cref="TptDog"/> instance.
    /// </summary>
    [Fact]
    public async Task GetAsync_Dog_BaseAndJoinColumnsLoaded()
    {
        await CreateTablesAsync();

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(new TptDog { Name = "Max", Breed = "Beagle" });
        await txn.CommitAsync();

        await using var readTxn = await Factory.OpenTransactionAsync();
        var dog = await readTxn.GetAsync<TptDog>(1);

        Assert.NotNull(dog);
        Assert.Equal(1,       dog.Id);
        Assert.Equal("Max",   dog.Name);
        Assert.Equal("Beagle", dog.Breed);
    }

    /// <summary>
    /// Changing only Breed after a GetAsync: the UPDATE must touch only the join
    /// table row; the base table Name must remain unchanged.
    /// </summary>
    [Fact]
    public async Task Update_DogBreed_OnlyJoinTableRowChanged()
    {
        await CreateTablesAsync();

        await using var insertTxn = await Factory.OpenTransactionAsync();
        insertTxn.Save(new TptDog { Name = "Luna", Breed = "Pug" });
        await insertTxn.CommitAsync();

        await using var updateTxn = await Factory.OpenTransactionAsync();
        var dog = await updateTxn.GetAsync<TptDog>(1);
        dog!.Breed = "Bulldog";
        updateTxn.Save(dog);
        await updateTxn.CommitAsync();

        Assert.Equal("Bulldog", await ScalarAsync("SELECT Breed FROM tpt_dogs    WHERE Id = 1"));
        Assert.Equal("Luna",    await ScalarAsync("SELECT Name  FROM tpt_animals WHERE Id = 1")); // unchanged
    }

    /// <summary>
    /// Changing only Name after a GetAsync: the UPDATE must touch only the base
    /// table row; the join table Breed must remain unchanged.
    /// </summary>
    [Fact]
    public async Task Update_DogName_OnlyBaseTableRowChanged()
    {
        await CreateTablesAsync();

        await using var insertTxn = await Factory.OpenTransactionAsync();
        insertTxn.Save(new TptDog { Name = "Charlie", Breed = "Husky" });
        await insertTxn.CommitAsync();

        await using var updateTxn = await Factory.OpenTransactionAsync();
        var dog = await updateTxn.GetAsync<TptDog>(1);
        dog!.Name = "Charlie II";
        updateTxn.Save(dog);
        await updateTxn.CommitAsync();

        Assert.Equal("Charlie II", await ScalarAsync("SELECT Name  FROM tpt_animals WHERE Id = 1"));
        Assert.Equal("Husky",      await ScalarAsync("SELECT Breed FROM tpt_dogs    WHERE Id = 1")); // unchanged
    }

    /// <summary>
    /// Deleting a dog with foreign-key enforcement on: the join row must be
    /// deleted before the base row to satisfy the FK constraint.
    /// </summary>
    [Fact]
    public async Task Delete_Dog_JoinRowDeletedFirstThenBaseRow()
    {
        await CreateTablesAsync();
        await ExecAsync("PRAGMA foreign_keys = ON");

        await using var insertTxn = await Factory.OpenTransactionAsync();
        insertTxn.Save(new TptDog { Name = "Ghost", Breed = "Shepherd" });
        await insertTxn.CommitAsync();

        Assert.Equal(1L, await CountAsync("tpt_animals"));

        await using var deleteTxn = await Factory.OpenTransactionAsync();
        var dog = await deleteTxn.GetAsync<TptDog>(1);
        deleteTxn.Delete(dog!);
        await deleteTxn.CommitAsync(); // would throw FK violation if join row deleted after base

        Assert.Equal(0L, await CountAsync("tpt_animals"));
        Assert.Equal(0L, await CountAsync("tpt_dogs"));
    }
}
