using PersistNet;

namespace PersistNet.Perf.PN;

[TableInfo(TableName = "perf_order_charge")]
public class PnOrderCharge
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int ChargeId { get; set; }

    [ColumnInfo]
    public int ItemLineId { get; set; }

    [ColumnInfo]
    public int ChargeIndex { get; set; }

    [ColumnInfo(ColumnType = ColumnType.Decimal)]
    public decimal ChargeValue { get; set; }
}
