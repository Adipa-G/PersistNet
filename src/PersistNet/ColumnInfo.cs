using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class ColumnInfo : Attribute
{
    public readonly ColumnType ColumnType;

    public ColumnInfo(ColumnType columnType, string columnName)
    {
        ColumnType = columnType;
        ColumnName = columnName;
    }

    public string ColumnName { get; set; }

    public bool Key { get; set; } =  false;

    public bool Nullable { get; set; } = false;

    public int? Size { get; set; }
}