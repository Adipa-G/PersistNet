using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenarios that verify the statement optimizer collapses UPDATE operations into the
/// minimum number of SQL statements and that both optimizer paths work end-to-end:
///
///   Homogeneous-version path — all rows in a group share the same expected version:
///     <c>UPDATE t SET col=@v, ver=@new WHERE key IN (…) AND ver=@old</c>
///
///   Mixed-version path — rows share the same data change but carry different version
///   baselines; the optimizer merges them into one statement:
///     <c>UPDATE t SET col=@v, ver=ver+1 WHERE (key,ver) IN ((k1,v1),(k2,v2),…)</c>
///     (SQL Server uses an OR-chain instead of a row-value constructor)
///
///
/// Schema:
///   sc_batch_items     (Id PK, Status TEXT, Version INTEGER)
///   sc_batch_items_ext (Id PK, Status TEXT, Priority TEXT, Version INTEGER)
/// </summary>
public sealed class BatchUpdateScenarioTests : ScenarioTestBase
{
    // ── Entity models ─────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_batch_items")]
    private class BatchItem
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Status { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }
    }

    /// <summary>Two non-key data columns so partial-field dirty-tracking can be exercised.</summary>
    [TableInfo(TableName = "sc_batch_items_ext")]
    private class ExtBatchItem
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Status { get; set; } = "";

        [ColumnInfo]
        public string Priority { get; set; } = "";

        [ColumnInfo(IsVersion = true)]
        public long Version { get; set; }
    }

    // ── SQL-counting logger ────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that counts UPDATE SQL statements issued by the
    /// persistence layer.  <see cref="AnsiSqlPersistenceBase"/> logs every execution at
    /// <c>Debug</c> level as <c>"Executing SQL: {sql} | Params: …"</c>, so any Debug
    /// message whose text begins <c>"Executing SQL: UPDATE"</c> is one UPDATE statement.
    /// </summary>
    private sealed class SqlCountingLogger : ILogger<TransactionFactory>
    {
        private int _updateCount;

        public int UpdateCount => _updateCount;

        IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        bool ILogger.IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Debug) return;
            var message = formatter(state, exception);
            if (message.StartsWith("Executing SQL: UPDATE", StringComparison.OrdinalIgnoreCase))
                _updateCount++;
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task CreateTableAsync()
        => await ExecAsync(
            "CREATE TABLE sc_batch_items " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Status TEXT NOT NULL, Version INTEGER NOT NULL)");

    private async Task CreateExtTableAsync()
        => await ExecAsync(
            "CREATE TABLE sc_batch_items_ext " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Status TEXT NOT NULL, " +
            "Priority TEXT NOT NULL, Version INTEGER NOT NULL)");

    private TransactionFactory CountingFactory(out SqlCountingLogger logger)
    {
        logger = new SqlCountingLogger();
        return new TransactionFactory(Connection, DbProvider.SQLite, logger);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Baseline: 10 versioned entities split into 2 groups of 5 by Status value.
    /// The optimizer must issue exactly 2 UPDATE statements — one per distinct
    /// SET-clause fingerprint — and every version must be incremented.
    /// </summary>
    [Fact]
    public async Task Save_TenVersionedEntities_TwoDistinctValues_EmitsTwoUpdateStatements()
    {
        await CreateTableAsync();
        for (int i = 1; i <= 10; i++)
            await ExecAsync($"INSERT INTO sc_batch_items VALUES ({i}, 'Initial', 1)");

        await using var txn = await CountingFactory(out var logger).OpenTransactionAsync();
        var items = await txn.Query<BatchItem>().ToListAsync();

        foreach (var item in items.Where(x => x.Id <= 5))  item.Status = "Active";
        foreach (var item in items.Where(x => x.Id >  5))  item.Status = "Archived";
        foreach (var item in items) txn.Save(item);

        await txn.CommitAsync();

        Assert.Equal(2, logger.UpdateCount);

        for (int id = 1;  id <= 5;  id++)
            Assert.Equal("Active",   await ScalarAsync($"SELECT Status FROM sc_batch_items WHERE Id={id}"));
        for (int id = 6;  id <= 10; id++)
            Assert.Equal("Archived", await ScalarAsync($"SELECT Status FROM sc_batch_items WHERE Id={id}"));
        for (int id = 1;  id <= 10; id++)
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items WHERE Id={id}")));
    }

    /// <summary>
    /// Mixed version baselines, same data change: entities at Version=1 and Version=2
    /// both get Status="Active".  The optimizer merges them into ONE statement using the
    /// mixed-version path (row-value constructor WHERE clause, Version = Version+1 in SET).
    /// Each entity's version is incremented relative to its own baseline.
    /// </summary>
    [Fact]
    public async Task Save_SameValueButDifferentVersionBaseline_EmitsOneUpdateStatement()
    {
        await CreateTableAsync();
        // ids 1–3 seeded at Version=1, ids 4–6 at Version=2.
        for (int i = 1; i <= 3; i++) await ExecAsync($"INSERT INTO sc_batch_items VALUES ({i}, 'Initial', 1)");
        for (int i = 4; i <= 6; i++) await ExecAsync($"INSERT INTO sc_batch_items VALUES ({i}, 'Initial', 2)");

        await using var txn = await CountingFactory(out var logger).OpenTransactionAsync();
        var items = await txn.Query<BatchItem>().ToListAsync();

        // All entities get the same new Status despite different version baselines.
        foreach (var item in items) item.Status = "Active";
        foreach (var item in items) txn.Save(item);

        await txn.CommitAsync();

        // Mixed-version path collapses all 6 rows into a single UPDATE statement.
        Assert.Equal(1, logger.UpdateCount);

        // Each entity's version is incremented from its own baseline, not a shared value.
        for (int id = 1; id <= 3; id++)
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items WHERE Id={id}")));
        for (int id = 4; id <= 6; id++)
            Assert.Equal(3L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items WHERE Id={id}")));
    }

    /// <summary>
    /// Two data columns, three distinct (Status, Priority) value combinations across
    /// 9 entities.  Without snapshot-based dirty tracking (loaded via Query), ALL
    /// non-key columns appear in every SET clause, so grouping is driven by the full
    /// (Status, Priority) tuple — producing exactly 3 UPDATE statements.
    /// </summary>
    [Fact]
    public async Task Save_TwoFields_ThreeDistinctCombinations_EmitsThreeUpdateStatements()
    {
        await CreateExtTableAsync();
        for (int i = 1; i <= 9; i++)
            await ExecAsync($"INSERT INTO sc_batch_items_ext VALUES ({i}, 'Initial', 'Default', 1)");

        await using var txn = await CountingFactory(out var logger).OpenTransactionAsync();
        var items = await txn.Query<ExtBatchItem>().ToListAsync();

        // Group A — ids 1–3: Status="Active",   Priority="Low"
        foreach (var item in items.Where(x => x.Id <= 3))
        { item.Status = "Active";   item.Priority = "Low";  }

        // Group B — ids 4–6: Status="Active",   Priority="High"
        foreach (var item in items.Where(x => x.Id is >= 4 and <= 6))
        { item.Status = "Active";   item.Priority = "High"; }

        // Group C — ids 7–9: Status="Archived", Priority="Low"
        foreach (var item in items.Where(x => x.Id >= 7))
        { item.Status = "Archived"; item.Priority = "Low";  }

        foreach (var item in items) txn.Save(item);

        await txn.CommitAsync();

        Assert.Equal(3, logger.UpdateCount);

        // Verify a representative row from each group.
        Assert.Equal("Active",   await ScalarAsync("SELECT Status   FROM sc_batch_items_ext WHERE Id=1"));
        Assert.Equal("Low",      await ScalarAsync("SELECT Priority FROM sc_batch_items_ext WHERE Id=1"));
        Assert.Equal("Active",   await ScalarAsync("SELECT Status   FROM sc_batch_items_ext WHERE Id=4"));
        Assert.Equal("High",     await ScalarAsync("SELECT Priority FROM sc_batch_items_ext WHERE Id=4"));
        Assert.Equal("Archived", await ScalarAsync("SELECT Status   FROM sc_batch_items_ext WHERE Id=7"));
        Assert.Equal("Low",      await ScalarAsync("SELECT Priority FROM sc_batch_items_ext WHERE Id=7"));

        // All versions must be incremented.
        for (int id = 1; id <= 9; id++)
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items_ext WHERE Id={id}")));
    }

    /// <summary>
    /// Dirty-tracking (snapshot captured by GetAsync) means only changed columns
    /// appear in each entity's SET clause.  9 entities split into three groups:
    ///   Group A (ids 1–4): only Status changed  → SET clause has Status only
    ///   Group B (ids 5–7): only Priority changed → SET clause has Priority only
    ///   Group C (ids 8–9): both fields changed   → SET clause has Status + Priority
    /// Different SET-clause shapes produce different fingerprints, so exactly 3
    /// UPDATE statements must be issued even though all 9 entities share the same
    /// starting version.
    /// </summary>
    [Fact]
    public async Task Save_DirtyTracked_PartialFieldUpdates_EmitsThreeUpdateStatements()
    {
        await CreateExtTableAsync();
        for (int i = 1; i <= 9; i++)
            await ExecAsync($"INSERT INTO sc_batch_items_ext VALUES ({i}, 'Initial', 'Default', 1)");

        await using var txn = await CountingFactory(out var logger).OpenTransactionAsync();

        // GetAsync snapshots each entity so only modified columns enter the SET clause.
        var loaded = new List<ExtBatchItem>();
        for (int i = 1; i <= 9; i++)
            loaded.Add(await txn.GetAsync<ExtBatchItem>(i));

        // Group A: only Status changes.
        foreach (var item in loaded.Where(x => x.Id <= 4))
            item.Status = "Active";

        // Group B: only Priority changes.
        foreach (var item in loaded.Where(x => x.Id is >= 5 and <= 7))
            item.Priority = "Low";

        // Group C: both fields change.
        foreach (var item in loaded.Where(x => x.Id >= 8))
        { item.Status = "Done"; item.Priority = "High"; }

        foreach (var item in loaded) txn.Save(item);

        await txn.CommitAsync();

        // Three distinct SET-clause shapes → 3 UPDATE statements.
        Assert.Equal(3, logger.UpdateCount);

        // Group A: Status updated, Priority untouched.
        for (int id = 1; id <= 4; id++)
        {
            Assert.Equal("Active",  await ScalarAsync($"SELECT Status   FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal("Default", await ScalarAsync($"SELECT Priority FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items_ext WHERE Id={id}")));
        }

        // Group B: Priority updated, Status untouched.
        for (int id = 5; id <= 7; id++)
        {
            Assert.Equal("Initial", await ScalarAsync($"SELECT Status   FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal("Low",     await ScalarAsync($"SELECT Priority FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items_ext WHERE Id={id}")));
        }

        // Group C: both fields updated.
        for (int id = 8; id <= 9; id++)
        {
            Assert.Equal("Done", await ScalarAsync($"SELECT Status   FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal("High", await ScalarAsync($"SELECT Priority FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal(2L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items_ext WHERE Id={id}")));
        }
    }

    /// <summary>
    /// When entities are loaded via GetAsync and saved without any modifications,
    /// dirty tracking detects no changed columns and suppresses the UPDATE entirely —
    /// zero SQL statements should be issued and the database must remain unchanged.
    /// </summary>
    [Fact]
    public async Task Save_DirtyTracked_NoFieldsModified_EmitsZeroUpdateStatements()
    {
        await CreateExtTableAsync();
        for (int i = 1; i <= 5; i++)
            await ExecAsync($"INSERT INTO sc_batch_items_ext VALUES ({i}, 'Initial', 'Default', 1)");

        await using var txn = await CountingFactory(out var logger).OpenTransactionAsync();

        for (int i = 1; i <= 5; i++)
        {
            var item = await txn.GetAsync<ExtBatchItem>(i);
            txn.Save(item);   // no changes — dirty check must suppress the UPDATE
        }

        await txn.CommitAsync();

        Assert.Equal(0, logger.UpdateCount);

        // Database must be completely unchanged.
        for (int id = 1; id <= 5; id++)
        {
            Assert.Equal("Initial", await ScalarAsync($"SELECT Status   FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal("Default", await ScalarAsync($"SELECT Priority FROM sc_batch_items_ext WHERE Id={id}"));
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync($"SELECT Version FROM sc_batch_items_ext WHERE Id={id}")));
        }
    }
}
