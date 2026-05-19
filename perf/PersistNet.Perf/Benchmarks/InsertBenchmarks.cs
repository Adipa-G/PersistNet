using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace PersistNet.Perf;

/// <summary>
/// Benchmarks bulk insert for PersistNet vs EF Core.
/// IterationCleanup deletes all inserted rows and reseeds identity counters
/// so each iteration inserts into a clean table with IDs starting from 1.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class InsertBenchmarks
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
        _dbName  = $"PerfInsert_{Framework}_{DateTime.UtcNow.Ticks}";
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

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Remove inserted rows and reset identity so the next iteration starts fresh.
        using var conn = new SqlConnection(_connStr);
        conn.Open();
        Exec(conn, "DELETE FROM perf_order_charge");
        Exec(conn, "DELETE FROM perf_order_item");
        Exec(conn, "DELETE FROM perf_order");
        Exec(conn, "DBCC CHECKIDENT ('perf_order_charge', RESEED, 0)");
        Exec(conn, "DBCC CHECKIDENT ('perf_order_item',   RESEED, 0)");
        Exec(conn, "DBCC CHECKIDENT ('perf_order',        RESEED, 0)");
    }

    [Benchmark]
    public async Task Insert()
    {
        if (Framework == "PersistNet")
            await _pnRunner.InsertAsync(_seeds);
        else
            await _efRunner.InsertAsync(_seeds);
    }

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
