using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet.Perf.PN;

namespace PersistNet.Perf;

public sealed record BenchOp(string Operation, int Rows, long Ms)
{
    public double RowsPerSec => Ms > 0 ? Rows * 1000.0 / Ms : 0;
}

public sealed class RunResult
{
    public string        Framework { get; init; } = "";
    public List<BenchOp> Ops       { get; init; } = new();
}

public sealed class PersistNetRunner
{
    private readonly TransactionFactory _factory;
    private readonly int _orderCount;
    private readonly int _itemsPerOrder;
    private readonly int _chargesPerItem;

    public PersistNetRunner(string connectionString, int orderCount, int itemsPerOrder, int chargesPerItem)
    {
        _orderCount     = orderCount;
        _itemsPerOrder  = itemsPerOrder;
        _chargesPerItem = chargesPerItem;

        _factory = new TransactionFactory(
            connectionString,
            SqlClientFactory.Instance,
            DbProvider.SqlServer,
            NullLogger<TransactionFactory>.Instance);
    }

    public async Task<RunResult> RunAsync(SeedOrder[] seeds)
    {
        int totalRows = seeds.Length
            + seeds.Length * _itemsPerOrder
            + seeds.Length * _itemsPerOrder * _chargesPerItem;

        int[] insertedIds = Array.Empty<int>();
        var ops = new List<BenchOp>
        {
            await Measure("Insert",        async () => { insertedIds = await InsertAsync(seeds); }, totalRows),
        };

        int[] sampleIds = Enumerable.Range(1, 50)
            .Select(i => insertedIds[(int)Math.Round(i * (insertedIds.Length - 1) / 50.0)])
            .ToArray();
        DateTime cutoff = DateTime.UtcNow.AddDays(-182);

        ops.AddRange(new[]
        {
            await Measure("Query (all)",   () => QueryAllAsync()),
            await Measure("Query (id)",    () => QueryByIdAsync(sampleIds)),
            await Measure("Query (cond)",  () => QueryByCondAsync(cutoff)),
            await Measure("Query (graph)", () => QueryGraphAsync()),
            await Measure("Update",        () => UpdateAsync(sampleIds)),
            await Measure("Delete",        () => DeleteAsync()),
        });

        return new RunResult { Framework = "PersistNet", Ops = ops };
    }

    // ── INSERT ────────────────────────────────────────────────────────────────

    private async Task<int[]> InsertAsync(SeedOrder[] seeds)
    {
        // 1. Insert orders; capture entities so OUTPUT INSERTED populates OrderId.
        var orders = new List<PnOrder>(seeds.Length);
        await using (var txn = await _factory.OpenTransactionAsync())
        {
            foreach (var s in seeds)
            {
                var o = new PnOrder { OrderId = 0, Name = s.Name, OrderDate = s.OrderDate };
                txn.Save(o);
                orders.Add(o);
            }
            await txn.CommitAsync();
        }

        // 2. Insert items; capture entities so OUTPUT INSERTED populates ItemLineId.
        var insertedItems = new List<PnOrderItem>(seeds.Length * _itemsPerOrder);
        await using (var txn = await _factory.OpenTransactionAsync())
        {
            for (int i = 0; i < seeds.Length; i++)
            {
                int orderId = orders[i].OrderId;
                for (int j = 0; j < seeds[i].Items.Length; j++)
                {
                    var item = seeds[i].Items[j];
                    var pnItem = new PnOrderItem
                    {
                        ItemLineId = 0,
                        OrderId    = orderId,
                        IndexNo    = j,
                        ItemId     = item.ProductItemId,
                        Quantity   = item.Quantity,
                    };
                    txn.Save(pnItem);
                    insertedItems.Add(pnItem);
                }
            }
            await txn.CommitAsync();
        }

        // 3. Insert charges using the populated ItemLineIds.
        await using (var txn = await _factory.OpenTransactionAsync())
        {
            int itemIdx = 0;
            for (int i = 0; i < seeds.Length; i++)
            {
                for (int j = 0; j < seeds[i].Items.Length; j++, itemIdx++)
                {
                    int itemLineId = insertedItems[itemIdx].ItemLineId;
                    var item = seeds[i].Items[j];
                    for (int k = 0; k < item.ChargeValues.Length; k++)
                    {
                        txn.Save(new PnOrderCharge
                        {
                            ChargeId    = 0,
                            ItemLineId  = itemLineId,
                            ChargeIndex = k,
                            ChargeValue = item.ChargeValues[k],
                        });
                    }
                }
            }
            await txn.CommitAsync();
        }

        return orders.Select(o => o.OrderId).ToArray();
    }

    // ── QUERY (all) ───────────────────────────────────────────────────────────

    private async Task<int> QueryAllAsync()
    {
        await using var txn = await _factory.OpenTransactionAsync();
        var orders = await txn.Query<PnOrder>().ToListAsync();
        await txn.CommitAsync();
        return orders.Count;
    }

    // ── QUERY (by id) ─────────────────────────────────────────────────────────

    private async Task<int> QueryByIdAsync(int[] ids)
    {
        await using var txn = await _factory.OpenTransactionAsync();
        foreach (var id in ids)
            _ = await txn.GetAsync<PnOrder>(id);
        await txn.CommitAsync();
        return ids.Length;
    }

    // ── QUERY (condition) ─────────────────────────────────────────────────────

    private async Task<int> QueryByCondAsync(DateTime cutoff)
    {
        await using var txn = await _factory.OpenTransactionAsync();
        var orders = await txn.QueryAsync<PnOrder>(
            "SELECT * FROM perf_order WHERE OrderDate >= @cutoff",
            new { cutoff });
        await txn.CommitAsync();
        return orders.Count;
    }

    // ── QUERY (full graph) ────────────────────────────────────────────────────

    private async Task<int> QueryGraphAsync()
    {
        await using var txn = await _factory.OpenTransactionAsync();
        var orders  = await txn.Query<PnOrder>().ToListAsync();
        var items   = await txn.Query<PnOrderItem>().ToListAsync();
        var charges = await txn.Query<PnOrderCharge>().ToListAsync();
        await txn.CommitAsync();
        return orders.Count + items.Count + charges.Count;
    }

    // ── UPDATE ────────────────────────────────────────────────────────────────

    private async Task<int> UpdateAsync(int[] sampleIds)
    {
        string idList = string.Join(",", sampleIds);

        await using var txn = await _factory.OpenTransactionAsync();

        var orders = await txn.QueryAsync<PnOrder>(
            $"SELECT * FROM perf_order WHERE OrderId IN ({idList})");
        var items = await txn.QueryAsync<PnOrderItem>(
            $"SELECT * FROM perf_order_item WHERE OrderId IN ({idList})");

        foreach (var o in orders)
        {
            o.Name = "Upd_" + o.OrderId;
            txn.Save(o);
        }

        foreach (var item in items)
        {
            item.Quantity += 1;
            txn.Save(item);
            txn.Save(new PnOrderCharge
            {
                ChargeId    = 0,
                ItemLineId  = item.ItemLineId,
                ChargeIndex = _chargesPerItem,
                ChargeValue = 0.99m,
            });
        }

        await txn.CommitAsync();
        return orders.Count + items.Count * 2;
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    private async Task<int> DeleteAsync()
    {
        int total = 0;

        await using (var txn = await _factory.OpenTransactionAsync())
        {
            var charges = await txn.Query<PnOrderCharge>().ToListAsync();
            foreach (var c in charges) txn.Delete(c);
            await txn.CommitAsync();
            total += charges.Count;
        }

        await using (var txn = await _factory.OpenTransactionAsync())
        {
            var items = await txn.Query<PnOrderItem>().ToListAsync();
            foreach (var i in items) txn.Delete(i);
            await txn.CommitAsync();
            total += items.Count;
        }

        await using (var txn = await _factory.OpenTransactionAsync())
        {
            var orders = await txn.Query<PnOrder>().ToListAsync();
            foreach (var o in orders) txn.Delete(o);
            await txn.CommitAsync();
            total += orders.Count;
        }

        return total;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<BenchOp> Measure(string name, Func<Task<int>> action)
    {
        var sw = Stopwatch.StartNew();
        int rows = await action();
        sw.Stop();
        return new BenchOp(name, rows, sw.ElapsedMilliseconds);
    }

    private static async Task<BenchOp> Measure(string name, Func<Task> action, int knownRows)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        return new BenchOp(name, knownRows, sw.ElapsedMilliseconds);
    }
}
