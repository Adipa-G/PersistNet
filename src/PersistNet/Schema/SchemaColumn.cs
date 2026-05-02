namespace PersistNet.Schema;

internal sealed class SchemaColumn
{
    public string Name { get; }

    /// <summary>
    /// Canonical database type string (e.g. "VARCHAR", "BIGINT", "DECIMAL").
    /// For columns derived from entity metadata this is mapped from <see cref="ColumnType"/>.
    /// For columns read from a live database this is the raw type string from the catalog.
    /// </summary>
    public string DbType { get; }

    public bool IsNullable { get; }
    public bool IsAutoIncrement { get; }
    public string? DefaultValue { get; }
    public int? Size { get; }
    public int? Precision { get; }
    public int? Scale { get; }

    internal SchemaColumn(
        string name,
        string dbType,
        bool isNullable,
        bool isAutoIncrement,
        string? defaultValue,
        int? size,
        int? precision,
        int? scale)
    {
        Name = name;
        DbType = dbType;
        IsNullable = isNullable;
        IsAutoIncrement = isAutoIncrement;
        DefaultValue = defaultValue;
        Size = size;
        Precision = precision;
        Scale = scale;
    }
}
