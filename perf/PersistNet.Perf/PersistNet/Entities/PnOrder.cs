using PersistNet;

namespace PersistNet.Perf.PN;

[TableInfo(TableName = "perf_order")]
public class PnOrder
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int OrderId { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    [ColumnInfo(ColumnType = ColumnType.Timestamp)]
    public DateTime OrderDate { get; set; }
}
