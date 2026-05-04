using System;
using System.Linq.Expressions;
using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class Column
{
    public PropertyInfo Property { get; }

    /// <summary>Compiled getter — avoids reflection overhead on every row.</summary>
    public Func<object, object?> Getter { get; }

    public string ColumnName { get; }
    public ColumnType? Type { get; }
    public bool IsKey { get; }
    public int KeyOrder { get; }
    public bool AutoIncrement { get; }
    public bool Nullable { get; }
    public bool Unique { get; }
    public bool IsDiscriminator { get; }
    public bool IsVersion { get; }
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
        bool isVersion,
        int? size,
        int? precision,
        int? scale,
        string? defaultValue)
    {
        Property = property;
        Getter = BuildGetter(property);
        ColumnName = columnName;
        Type = type;
        IsKey = isKey;
        KeyOrder = keyOrder;
        AutoIncrement = autoIncrement;
        Nullable = nullable;
        Unique = unique;
        IsDiscriminator = isDiscriminator;
        IsVersion = isVersion;
        Size = size;
        Precision = precision;
        Scale = scale;
        DefaultValue = defaultValue;
    }

    private static Func<object, object?> BuildGetter(PropertyInfo property)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var cast = Expression.Convert(param, property.DeclaringType!);
        var access = Expression.Property(cast, property);
        var box = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(box, param).Compile();
    }
}
