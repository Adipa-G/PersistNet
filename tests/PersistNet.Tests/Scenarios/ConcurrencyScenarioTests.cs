using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Optimistic concurrency via version columns.
///
/// Schema:
///   cv_orders  (Id PK, Status TEXT, Version INTEGER)
///   cv_lines   (Id PK, OrderId FK, Note TEXT, Version INTEGER)
///
/// Tests verify that a version mismatch between load time and commit time:
///   1. Throws <see cref="ConcurrencyException"/> on the root entity.
///   2. Rolls back the transaction — no partial writes survive.
///   3. Throws <see cref="ConcurrencyException"/> when the stale entity is a
///      child loaded as part of an entity graph.
/// </summary>
public sealed class ConcurrencyScenarioTests : ScenarioTestBase
{
    // ── Entity models ────────────────────────────────────────────────────────

    [TableInfo(TableName = "cv_orders")]
    private class CvOrder
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Status { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }

        [OneToManyRelationshipInfo(RelatedType = typeof(CvLine), MappedBy = "Order")]
        public List<CvLine>? Lines { get; set; }
    }

    [TableInfo(TableName = "cv_lines")]
    private class CvLine
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int OrderId { get; set; }

        [ColumnInfo]
        public string Note { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(CvOrder),
            FromKeys = new[] { "OrderId" },
            ToKeys = new[] { "Id" })]
        public CvOrder? Order { get; set; }
    }

    // ── DDL helpers ──────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE cv_orders " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Status TEXT NOT NULL, Version INTEGER NOT NULL)");
        await ExecAsync(
            "CREATE TABLE cv_lines " +
            "(Id INTEGER NOT NULL PRIMARY KEY, OrderId INTEGER NOT NULL, " +
            "Note TEXT NOT NULL, Version INTEGER NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a root entity via GetAsync, simulate a concurrent update that bumps its
    /// version, then commit our modification — ConcurrencyException must be thrown.
    /// </summary>
    [Fact]
    public async Task GetAsync_LoadThenConcurrentRootUpdate_CommitThrowsConcurrencyException()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO cv_orders VALUES (1, 'Pending', 1)");

        await using var txn = await Factory.OpenTransactionAsync();
        var order = await txn.GetAsync<CvOrder>(1);   // snapshot: Version = 1

        // Simulate a concurrent writer bumping the version.
        await ExecAsync("UPDATE cv_orders SET Status = 'Cancelled', Version = 2 WHERE Id = 1");

        order.Status = "Approved";                    // our change, carrying stale Version = 1
        txn.Save(order);

        await Assert.ThrowsAsync<ConcurrencyException>(() => txn.CommitAsync());
    }

    /// <summary>
    /// When the child UPDATE fails with a ConcurrencyException, the whole transaction
    /// must be rolled back — including the parent UPDATE that already executed successfully
    /// within the same transaction before the exception was thrown.
    /// </summary>
    [Fact]
    public async Task ConcurrencyException_OnChildWrite_ParentChangeAlsoRolledBack()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO cv_orders VALUES (1, 'Pending', 1)");
        await ExecAsync("INSERT INTO cv_lines VALUES (1, 1, 'Widget', 1)");

        // Construct the failing transaction.
        // cv_orders is processed first (topo-sort: cv_lines depends on cv_orders).
        //   • Parent update uses the correct version (1) → succeeds within the transaction.
        //   • Child update uses a stale version (99) → 0 rows affected → ConcurrencyException.
        {
            await using var txn = await Factory.OpenTransactionAsync();
            txn.Save(new CvOrder { Id = 1, Status = "Processing", Version = 1 });  // valid → succeeds
            txn.Save(new CvLine  { Id = 1, OrderId = 1, Note = "Updated", Version = 99 }); // stale → fails

            await Assert.ThrowsAsync<ConcurrencyException>(() => txn.CommitAsync());
        } // DisposeAsync rolls back the entire transaction, including the parent's write.

        // The parent's "Processing" change must NOT have persisted.
        await using var readTxn = await Factory.OpenTransactionAsync();
        var reloaded = await readTxn.GetAsync<CvOrder>(1);

        Assert.Equal("Pending", reloaded.Status);
        Assert.Equal(1L,        reloaded.Version);
    }

    /// <summary>
    /// Load a parent+child graph, simulate a concurrent update that bumps the child's
    /// version only, then save both parent and child — ConcurrencyException must be
    /// thrown because the child UPDATE finds zero matching rows.
    /// The parent update (version unchanged) would succeed, but the whole transaction
    /// rolls back when DisposeAsync is called after the exception.
    /// </summary>
    [Fact]
    public async Task GetAsync_LoadGraphWithVersionedChild_ConcurrentChildUpdate_CommitThrowsConcurrencyException()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO cv_orders VALUES (1, 'Pending', 1)");
        await ExecAsync("INSERT INTO cv_lines VALUES (1, 1, 'Widget x1', 1)");

        await using var txn = await Factory.OpenTransactionAsync();
        var order = await txn.GetAsync<CvOrder>(1)
            .Include(o => o.Lines);                   // child snapshot: Version = 1

        // Simulate a concurrent writer bumping only the child's version.
        await ExecAsync("UPDATE cv_lines SET Note = 'Gadget x2', Version = 2 WHERE Id = 1");

        // Modify both parent and the now-stale child.
        order.Status = "Processing";
        var line = order.Lines![0];
        line.Note = "Widget x3";

        txn.Save(order);
        txn.Save(line);                               // still carries stale Version = 1

        await Assert.ThrowsAsync<ConcurrencyException>(() => txn.CommitAsync());
    }
}
