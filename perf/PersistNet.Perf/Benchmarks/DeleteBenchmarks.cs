using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace PersistNet.Perf;

/// <summary>
/// Benchmarks deleting the full order/item/charge dataset for PersistNet vs EF Core.
/// IterationSetup re-inserts all rows before each iteration so each benchmark
/// call starts with the same 7,000-row dataset to delete.
/// Identity counters are reseeded to 0 in IterationSetup so IDs stay predictable.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class DeleteBenchmarks
{
    [Params("PersistNet", "EFCore")]
    public string Framework { get; set; } = null!;

    private PersistNetRunner _pnRunner = null!;
    private EfRunner         _efRunner = null!;
    private SeedOrder[]      _seeds    = null!;
    private string           _connStr  = null!;
    private string           _dbName   = null!;

    private const int OrderCount     = 1000;
    private const int ItemsPerOrder  = 2;
    private const int ChargesPerItem = 2;

    [GlobalSetup]
    public void Setup()
    {
        _dbName  = $"PerfDelete_{Framework}_{DateTime.UtcNow.Ticks}";
        _connStr = SchemaHelper.CreateDatabase(_dbName);

        using var conn = new SqlConnection(_connStr);
        conn.Open();
        SchemaHelper.CreateSchema(conn);
        var (products, seeds) = DataFactory.Generate(200, OrderCount, ItemsPerOrder, ChargesPerItem);
        _seeds = seeds;
        SchemaHelper.SeedProductsAsync(conn, products).GetAwaiter().GetResult();

        if (Framework == "PersistNet")
            _pnRunner = new PersistNetRunner(_connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
        else
            _efRunner = new EfRunner(_connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
    }

    [IterationSetup]
    public void InsertData()
    {
        // Reset identity counters then re-insert the full dataset so the benchmark
        // always deletes exactly 7,000 rows regardless of how many iterations have run.
        using (var conn = new SqlConnection(_connStr))
        {
            conn.Open();
            Exec(conn, "DBCC CHECKIDENT ('perf_order_charge', RESEED, 0)");
            Exec(conn, "DBCC CHECKIDENT ('perf_order_item',   RESEED, 0)");
            Exec(conn, "DBCC CHECKIDENT ('perf_order',        RESEED, 0)");
        }

        if (Framework == "PersistNet")
            _pnRunner.InsertAsync(_seeds).GetAwaiter().GetResult();
        else
            _efRunner.InsertAsync(_seeds).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<int> Delete() => Framework == "PersistNet"
        ? _pnRunner.DeleteAsync()
        : _efRunner.DeleteAsync();

    [GlobalCleanup]
    public void Cleanup()
    {
        try { SchemaHelper.DropDatabase(_dbName); } catch { /* best-effort */ }
    }

    private static void Exec(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
