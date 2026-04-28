using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class ColumnInfo : Attribute
{
    public ColumnType? ColumnType { get; set; }

    public string? ColumnName { get; set; }

    public bool Key { get; set; } = false;

    public int KeyOrder { get; set; } = 0;

    public bool AutoIncrement { get; set; } = false;

    public bool Nullable { get; set; } = false;

    public bool Unique { get; set; } = false;

    public int? Size { get; set; }

    public int? Precision { get; set; }

    public int? Scale { get; set; }

    public string? DefaultValue { get; set; }
}