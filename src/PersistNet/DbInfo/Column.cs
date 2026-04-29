using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class Column
{
    public PropertyInfo Property { get; }
    public string ColumnName { get; }
    public ColumnType? Type { get; }
    public bool IsKey { get; }
    public int KeyOrder { get; }
    public bool AutoIncrement { get; }
    public bool Nullable { get; }
    public bool Unique { get; }
    public bool IsDiscriminator { get; }
    public int? Size { get; }
    public int? Precision { get; }
    public int? Scale { get; }
    public string? DefaultValue { get; }

    internal Column(
        PropertyInfo property,
        string columnName,
        ColumnType? type,
        bool isKey,
        int keyOrder,
        bool autoIncrement,
        bool nullable,
        bool unique,
        bool isDiscriminator,
        int? size,
        int? precision,
        int? scale,
        string? defaultValue)
    {
        Property = property;
        ColumnName = columnName;
        Type = type;
        IsKey = isKey;
        KeyOrder = keyOrder;
        AutoIncrement = autoIncrement;
        Nullable = nullable;
        Unique = unique;
        IsDiscriminator = isDiscriminator;
        Size = size;
        Precision = precision;
        Scale = scale;
        DefaultValue = defaultValue;
    }
}
