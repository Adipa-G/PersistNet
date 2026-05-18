using Microsoft.Data.SqlClient;
using PersistNet.Perf;

const int ProductCount   = 200;
const int OrderCount     = 1000;
const int ItemsPerOrder  = 2;
const int ChargesPerItem = 2;

int totalRows  = OrderCount + OrderCount * ItemsPerOrder + OrderCount * ItemsPerOrder * ChargesPerItem;

var dbName = $"PerfTest_{DateTime.UtcNow.Ticks}";
Console.WriteLine($"Creating database [{dbName}] on (localdb)\\MSSQLLocalDB...");

string connStr = "";
try
{
    connStr = SchemaHelper.CreateDatabase(dbName);

    await using (var conn = new SqlConnection(connStr))
    {
        await conn.OpenAsync();
        SchemaHelper.CreateSchema(conn);

        var (products, orders) = DataFactory.Generate(ProductCount, OrderCount, ItemsPerOrder, ChargesPerItem);
        await SchemaHelper.SeedProductsAsync(conn, products);

        Console.WriteLine(
            $"Schema ready. {products.Length} products seeded. " +
            $"Benchmarking {OrderCount:N0} orders × {ItemsPerOrder} items × {ChargesPerItem} charges " +
            $"= {totalRows:N0} rows.\n");
    }

    var results = new List<RunResult>();

    Console.Write("Running PersistNet... ");
    var pnRunner = new PersistNetRunner(connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
    results.Add(await pnRunner.RunAsync(DataFactory.Generate(ProductCount, OrderCount, ItemsPerOrder, ChargesPerItem).Orders));
    Console.WriteLine("done.");

    Console.Write("Running EF Core...    ");
    var efRunner = new EfRunner(connStr, OrderCount, ItemsPerOrder, ChargesPerItem);
    results.Add(await efRunner.RunAsync(DataFactory.Generate(ProductCount, OrderCount, ItemsPerOrder, ChargesPerItem).Orders));
    Console.WriteLine("done.");

    PrintResults(results);
}
finally
{
    if (connStr.Length > 0)
    {
        Console.WriteLine("\nDropping database...");
        try   { SchemaHelper.DropDatabase(dbName); Console.WriteLine("Done."); }
        catch (Exception ex) { Console.WriteLine($"Warning: could not drop database: {ex.Message}"); }
    }
}

static void PrintResults(List<RunResult> results)
{
    Console.WriteLine();
    Console.WriteLine($"{"Framework",-12} {"Operation",-14} {"Rows",8} {"ms",10} {"Rows/sec",12}");
    Console.WriteLine(new string('-', 62));

    foreach (var r in results)
    {
        foreach (var op in r.Ops)
        {
            double rps = op.Ms > 0 ? op.Rows * 1000.0 / op.Ms : 0;
            Console.WriteLine($"{r.Framework,-12} {op.Operation,-14} {op.Rows,8:N0} {op.Ms,10:N0} {rps,12:N0}");
        }
        Console.WriteLine();
    }
}
