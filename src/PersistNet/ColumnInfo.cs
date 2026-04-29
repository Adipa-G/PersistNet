using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class ColumnInfo : Attribute
{
    /// <summary>
    /// The database column type. Defaults to <see cref="ColumnType.Unknown"/> meaning "not specified".
    /// </summary>
    public ColumnType ColumnType { get; set; } = ColumnType.Unknown;

    public string? ColumnName { get; set; }

    public bool Key { get; set; } = false;

    public int KeyOrder { get; set; } = 0;

    public bool AutoIncrement { get; set; } = false;

    public bool Nullable { get; set; } = false;

    public bool Unique { get; set; } = false;

    public bool IsDiscriminator { get; set; } = false;

    /// <summary>
    /// Maximum length / size. Set to -1 (default) to leave unspecified.
    /// </summary>
    public int Size { get; set; } = -1;

    /// <summary>
    /// Numeric precision. Set to -1 (default) to leave unspecified.
    /// </summary>
    public int Precision { get; set; } = -1;

    /// <summary>
    /// Numeric scale. Set to -1 (default) to leave unspecified.
    /// </summary>
    public int Scale { get; set; } = -1;

    public string? DefaultValue { get; set; }
}