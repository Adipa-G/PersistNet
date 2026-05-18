using PersistNet;

namespace PersistNet.Perf.PN;

[TableInfo(TableName = "perf_order_item")]
public class PnOrderItem
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int ItemLineId { get; set; }

    [ColumnInfo]
    public int OrderId { get; set; }

    [ColumnInfo]
    public int IndexNo { get; set; }

    [ColumnInfo]
    public int ItemId { get; set; }

    [ColumnInfo]
    public int Quantity { get; set; }
}
