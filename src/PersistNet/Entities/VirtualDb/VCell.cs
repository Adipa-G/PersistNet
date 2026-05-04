namespace PersistNet.Entities.VirtualDb;

internal class VCell
{
    public string ColumnName { get; }
    public object? Value { get; set; }

    /// <summary>
    /// True when this cell represents a version/row-stamp column.
    /// <see cref="StatementOptimizer"/> uses this to split the cell into a SET (value+1)
    /// and a WHERE condition (current value) for optimistic concurrency.
    /// </summary>
    public bool IsVersion { get; init; }

    public VCell(string columnName, object? value)
    {
        ColumnName = columnName;
        Value = value;
    }
}