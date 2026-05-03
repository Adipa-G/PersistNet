namespace PersistNet.Entities.VirtualDb;

internal class VCell
{
    public string ColumnName { get; }
    public object? Value { get; set; }

    public VCell(string columnName, object? value)
    {
        ColumnName = columnName;
        Value = value;
    }
}