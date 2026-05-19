using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace PersistNet.Perf;

/// <summary>
/// Benchmarks read-only query operations for PersistNet vs EF Core.
/// Data is inserted once in GlobalSetup and reused across all iterations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class ReadBenchmarks
{
    [Params("PersistNet", "EFCore")]
    public string Framework { get; set; } = null!;

    private PersistNetRunner _pnRunner = null!;
    private EfRunner         _efRunner = null!;
    private string           _dbName   = null!;
    private int[]            _sampleIds = null!;
    private DateTime         _cutoff;

    private const int OrderCount     = 1000;
    private const int ItemsPerOrder  = 2;
    private const int ChargesPerItem = 2;

    [GlobalSetup]
    public void Setup()
    {
        // BDN runs GlobalSetup once per Params value → each framework gets an isolated DB.
        _dbName = $"PerfRead_{Framework}_{DateTime.UtcNow.Ticks}";
        var connStr = SchemaHelper.CreateDatabase(_dbName);

        using (var conn = new SqlConnection(connStr))
        {
            conn.Open();
            SchemaHelper.CreateSchema(conn);
            var (products, seeds) = DataFactory.Generate(200, OrderCount, ItemsPerOrder, ChargesPerItem);
            SchemaHelper.SeedProductsAsync(conn, products).GetAwaiter().GetResult();

            int[] insertedIds;
            if (Framework == "PersistNet")
            {
                _pnRunner  = new PersistNetRunner(connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
                insertedIds = _pnRunner.InsertAsync(seeds).GetAwaiter().GetResult();
            }
            else
            {
                _efRunner  = new EfRunner(connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
                insertedIds = _efRunner.InsertAsync(seeds).GetAwaiter().GetResult();
            }

            _sampleIds = Enumerable.Range(1, 50)
                .Select(i => insertedIds[(int)Math.Round(i * (insertedIds.Length - 1) / 50.0)])
                .ToArray();
            _cutoff = DateTime.UtcNow.AddDays(-182);
        }
    }

    [Benchmark]
    public Task<int> QueryAll() => Framework == "PersistNet"
        ? _pnRunner.QueryAllAsync()
        : _efRunner.QueryAllAsync();

    [Benchmark]
    public Task<int> QueryById() => Framework == "PersistNet"
        ? _pnRunner.QueryByIdAsync(_sampleIds)
        : _efRunner.QueryByIdAsync(_sampleIds);

    [Benchmark]
    public Task<int> QueryCond() => Framework == "PersistNet"
        ? _pnRunner.QueryByCondAsync(_cutoff)
        : _efRunner.QueryByCondAsync(_cutoff);

    [Benchmark]
    public Task<int> QueryGraph() => Framework == "PersistNet"
        ? _pnRunner.QueryGraphAsync()
        : _efRunner.QueryGraphAsync();

    [GlobalCleanup]
    public void Cleanup()
    {
        try { SchemaHelper.DropDatabase(_dbName); } catch { /* best-effort */ }
    }
}
