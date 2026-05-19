using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace PersistNet.Perf;

/// <summary>
/// Benchmarks the update operation (rename 50 orders, increment item quantities,
/// add one extra charge per item) for PersistNet vs EF Core.
///
/// The full 1,000-order dataset is inserted once in GlobalSetup.
/// IterationSetup resets only the 50 affected rows via direct SQL so each
/// iteration starts from an identical pre-update state without reinserting
/// the entire dataset.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class UpdateBenchmarks
{
    [Params("PersistNet", "EFCore")]
    public string Framework { get; set; } = null!;

    private PersistNetRunner _pnRunner      = null!;
    private EfRunner         _efRunner      = null!;
    private string           _connStr       = null!;
    private string           _dbName        = null!;
    private int[]            _sampleIds     = null!;
    private string           _orderIdList   = null!;  // comma-separated for SQL
    private string           _itemIdList    = null!;  // comma-separated for SQL

    private const int OrderCount     = 1000;
    private const int ItemsPerOrder  = 2;
    private const int ChargesPerItem = 2;   // must match runner constant

    [GlobalSetup]
    public void Setup()
    {
        _dbName  = $"PerfUpdate_{Framework}_{DateTime.UtcNow.Ticks}";
        _connStr = SchemaHelper.CreateDatabase(_dbName);

        using var conn = new SqlConnection(_connStr);
        conn.Open();
        SchemaHelper.CreateSchema(conn);
        var (products, seeds) = DataFactory.Generate(200, OrderCount, ItemsPerOrder, ChargesPerItem);
        SchemaHelper.SeedProductsAsync(conn, products).GetAwaiter().GetResult();

        int[] insertedIds;
        if (Framework == "PersistNet")
        {
            _pnRunner   = new PersistNetRunner(_connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
            insertedIds = _pnRunner.InsertAsync(seeds).GetAwaiter().GetResult();
        }
        else
        {
            _efRunner   = new EfRunner(_connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
            insertedIds = _efRunner.InsertAsync(seeds).GetAwaiter().GetResult();
        }

        _sampleIds   = Enumerable.Range(1, 50)
            .Select(i => insertedIds[(int)Math.Round(i * (insertedIds.Length - 1) / 50.0)])
            .ToArray();
        _orderIdList = string.Join(",", _sampleIds);

        // Resolve ItemLineIds for the 50 sample orders.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT ItemLineId FROM perf_order_item WHERE OrderId IN ({_orderIdList})";
        var itemIds = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) itemIds.Add(reader.GetInt32(0));
        _itemIdList = string.Join(",", itemIds);
    }

    [IterationSetup]
    public void ResetAffectedRows()
    {
        // Undo exactly what UpdateAsync does to the 50 sample orders:
        //   1. Remove the extra charge added per item (ChargeIndex = ChargesPerItem = 2).
        //   2. Decrement item quantities back by 1.
        //   3. Reset order names to a deterministic value.
        using var conn = new SqlConnection(_connStr);
        conn.Open();
        Exec(conn, $"DELETE FROM perf_order_charge WHERE ChargeIndex = {ChargesPerItem} AND ItemLineId IN ({_itemIdList})");
        Exec(conn, $"UPDATE perf_order_item SET Quantity = Quantity - 1 WHERE ItemLineId IN ({_itemIdList})");
        Exec(conn, $"UPDATE perf_order SET Name = 'Init_' + CAST(OrderId AS NVARCHAR(10)) WHERE OrderId IN ({_orderIdList})");
    }

    [Benchmark]
    public Task<int> Update() => Framework == "PersistNet"
        ? _pnRunner.UpdateAsync(_sampleIds)
        : _efRunner.UpdateAsync(_sampleIds);

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
