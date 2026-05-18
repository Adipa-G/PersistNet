using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PersistNet.Perf.EF;

namespace PersistNet.Perf;

public sealed class EfRunner
{
    private readonly string _connectionString;
    private readonly int _orderCount;
    private readonly int _itemsPerOrder;
    private readonly int _chargesPerItem;

    public EfRunner(string connectionString, int orderCount, int itemsPerOrder, int chargesPerItem)
    {
        _connectionString = connectionString;
        _orderCount       = orderCount;
        _itemsPerOrder    = itemsPerOrder;
        _chargesPerItem   = chargesPerItem;
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

        return new RunResult { Framework = "EF Core", Ops = ops };
    }

    // ── INSERT ────────────────────────────────────────────────────────────────

    private async Task<int[]> InsertAsync(SeedOrder[] seeds)
    {
        var efOrders = seeds
            .Select(s => new EfOrder { Name = s.Name, OrderDate = s.OrderDate })
            .ToArray();

        await using (var ctx = CreateContext())
        {
            ctx.Orders.AddRange(efOrders);
            await ctx.SaveChangesAsync();
        }

        var efItems = new List<EfOrderItem>(seeds.Length * _itemsPerOrder);
        for (int i = 0; i < seeds.Length; i++)
        {
            int orderId = efOrders[i].OrderId;
            for (int j = 0; j < seeds[i].Items.Length; j++)
            {
                var item = seeds[i].Items[j];
                efItems.Add(new EfOrderItem
                {
                    OrderId  = orderId,
                    IndexNo  = j,
                    ItemId   = item.ProductItemId,
                    Quantity = item.Quantity,
                });
            }
        }

        await using (var ctx = CreateContext())
        {
            ctx.OrderItems.AddRange(efItems);
            await ctx.SaveChangesAsync();
        }

        var efCharges = new List<EfOrderCharge>(efItems.Count * _chargesPerItem);
        int itemIdx = 0;
        for (int i = 0; i < seeds.Length; i++)
        {
            for (int j = 0; j < seeds[i].Items.Length; j++, itemIdx++)
            {
                int itemLineId = efItems[itemIdx].ItemLineId;
                var chargeValues = seeds[i].Items[j].ChargeValues;
                for (int k = 0; k < chargeValues.Length; k++)
                {
                    efCharges.Add(new EfOrderCharge
                    {
                        ItemLineId  = itemLineId,
                        ChargeIndex = k,
                        ChargeValue = chargeValues[k],
                    });
                }
            }
        }

        await using (var ctx = CreateContext())
        {
            ctx.OrderCharges.AddRange(efCharges);
            await ctx.SaveChangesAsync();
        }

        return efOrders.Select(o => o.OrderId).ToArray();
    }

    // ── QUERY (all) ───────────────────────────────────────────────────────────

    private async Task<int> QueryAllAsync()
    {
        await using var ctx = CreateContext();
        var orders = await ctx.Orders.AsNoTracking().ToListAsync();
        return orders.Count;
    }

    // ── QUERY (by id) ─────────────────────────────────────────────────────────

    private async Task<int> QueryByIdAsync(int[] ids)
    {
        await using var ctx = CreateContext();
        foreach (var id in ids)
            _ = await ctx.Orders.AsNoTracking().Where(o => o.OrderId == id).FirstOrDefaultAsync();
        return ids.Length;
    }

    // ── QUERY (condition) ─────────────────────────────────────────────────────

    private async Task<int> QueryByCondAsync(DateTime cutoff)
    {
        await using var ctx = CreateContext();
        var orders = await ctx.Orders.AsNoTracking().Where(o => o.OrderDate >= cutoff).ToListAsync();
        return orders.Count;
    }

    // ── QUERY (full graph) ────────────────────────────────────────────────────

    private async Task<int> QueryGraphAsync()
    {
        await using var ctx = CreateContext();
        var orders = await ctx.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Items)
            .ThenInclude(i => i.Charges)
            .ToListAsync();

        int total = orders.Count;
        foreach (var o in orders)
        {
            total += o.Items.Count;
            foreach (var item in o.Items)
                total += item.Charges.Count;
        }
        return total;
    }

    // ── UPDATE ────────────────────────────────────────────────────────────────

    private async Task<int> UpdateAsync(int[] sampleIds)
    {
        await using var ctx = CreateContext();
        var orders = await ctx.Orders
            .Include(o => o.Items)
            .Where(o => sampleIds.Contains(o.OrderId))
            .ToListAsync();

        int itemCount = 0;
        foreach (var o in orders)
        {
            o.Name = "Upd_" + o.OrderId;
            foreach (var item in o.Items)
            {
                item.Quantity += 1;
                ctx.OrderCharges.Add(new EfOrderCharge
                {
                    ItemLineId  = item.ItemLineId,
                    ChargeIndex = _chargesPerItem,
                    ChargeValue = 0.99m,
                });
                itemCount++;
            }
        }

        await ctx.SaveChangesAsync();
        return orders.Count + itemCount * 2;
    }

    // ── DELETE ────────────────────────────────────────────────────────────────

    private async Task<int> DeleteAsync()
    {
        int total = 0;

        await using (var ctx = CreateContext())
        {
            var charges = await ctx.OrderCharges.ToListAsync();
            ctx.OrderCharges.RemoveRange(charges);
            await ctx.SaveChangesAsync();
            total += charges.Count;
        }

        await using (var ctx = CreateContext())
        {
            var items = await ctx.OrderItems.ToListAsync();
            ctx.OrderItems.RemoveRange(items);
            await ctx.SaveChangesAsync();
            total += items.Count;
        }

        await using (var ctx = CreateContext())
        {
            var orders = await ctx.Orders.ToListAsync();
            ctx.Orders.RemoveRange(orders);
            await ctx.SaveChangesAsync();
            total += orders.Count;
        }

        return total;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private PerfDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PerfDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        return new PerfDbContext(options);
    }

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
