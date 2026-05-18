using PersistNet;

namespace PersistNet.Perf.PN;

// Products are seeded via raw SQL; this entity class is needed only so that
// DbInfoCache can resolve the perf_product table when compiling JOIN queries.
[TableInfo(TableName = "perf_product")]
public class PnProduct
{
    [ColumnInfo(Key = true)]
    public int ItemId { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    [ColumnInfo]
    public string Category { get; set; } = "";

    [ColumnInfo(ColumnType = ColumnType.Decimal)]
    public decimal UnitPrice { get; set; }

    [ColumnInfo(ColumnType = ColumnType.Decimal, Nullable = true)]
    public decimal? BulkUnitPrice { get; set; }
}
