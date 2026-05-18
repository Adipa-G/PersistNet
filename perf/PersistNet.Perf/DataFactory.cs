namespace PersistNet.Perf;

public sealed record SeedProduct(
    int      ItemId,
    string   Name,
    string   Category,
    decimal  UnitPrice,
    decimal? BulkUnitPrice);

public sealed record SeedOrderItem(
    int       ProductItemId,
    int       Quantity,
    decimal[] ChargeValues);

public sealed record SeedOrder(
    string         Name,
    DateTime       OrderDate,
    SeedOrderItem[] Items);

public static class DataFactory
{
    public static (SeedProduct[] Products, SeedOrder[] Orders) Generate(
        int productCount   = 200,
        int orderCount     = 1000,
        int itemsPerOrder  = 2,
        int chargesPerItem = 2)
    {
        var rng = new Random(42); // deterministic seed for reproducibility

        var products = Enumerable.Range(1, productCount)
            .Select(i => new SeedProduct(
                i,
                $"Product_{i:D4}",
                $"Category_{(i % 10) + 1:D2}",
                Math.Round((decimal)(rng.NextDouble() * 99 + 1), 4),
                rng.Next(2) == 1
                    ? Math.Round((decimal)(rng.NextDouble() * 89), 4)
                    : (decimal?)null))
            .ToArray();

        var orders = Enumerable.Range(1, orderCount)
            .Select(oi => new SeedOrder(
                $"Order_{oi:D5}",
                DateTime.UtcNow.AddDays(-rng.Next(365)),
                Enumerable.Range(0, itemsPerOrder)
                    .Select(ii => new SeedOrderItem(
                        (oi + ii) % productCount + 1,   // cycles through ProductItemId 1..productCount
                        rng.Next(1, 10),
                        Enumerable.Range(0, chargesPerItem)
                            .Select(_ => Math.Round((decimal)(rng.NextDouble() * 50), 4))
                            .ToArray()))
                    .ToArray()))
            .ToArray();

        return (products, orders);
    }
}
