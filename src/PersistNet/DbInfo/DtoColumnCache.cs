using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PersistNet.DbInfo;

/// <summary>
/// Caches the column metadata extracted from DTO/model types that carry
/// <see cref="ColumnInfo"/> attributes but no <see cref="TableInfo"/>.
/// Mirrors <see cref="DbInfoCache"/> but operates on a single type rather than
/// a full entity assembly scan.
/// </summary>
internal static class DtoColumnCache
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<Column>> _cache = new();

    /// <summary>
    /// Returns the cached column list for <paramref name="type"/>, extracting it on
    /// first access.  Only public instance properties decorated with
    /// <see cref="ColumnInfo"/> are included; all others are silently ignored.
    /// </summary>
    public static IReadOnlyList<Column> GetOrExtract(Type type)
        => _cache.GetOrAdd(type, BuildColumns);

    private static IReadOnlyList<Column> BuildColumns(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Select(p => (Prop: p, Attr: p.GetCustomAttribute<ColumnInfo>()))
               .Where(x => x.Attr is not null)
               .Select(x => new Column(
                   x.Prop,
                   x.Attr!.ColumnName ?? x.Prop.Name,
                   x.Attr.ColumnType == ColumnType.Unknown ? null : x.Attr.ColumnType,
                   isKey:           x.Attr.Key,
                   keyOrder:        x.Attr.KeyOrder,
                   autoIncrement:   x.Attr.AutoIncrement,
                   nullable:        x.Attr.Nullable,
                   unique:          x.Attr.Unique,
                   isDiscriminator: x.Attr.IsDiscriminator,
                   isVersion:       x.Attr.IsVersion,
                   size:            x.Attr.Size < 0 ? null : (int?)x.Attr.Size,
                   precision:       x.Attr.Precision < 0 ? null : (int?)x.Attr.Precision,
                   scale:           x.Attr.Scale < 0 ? null : (int?)x.Attr.Scale,
                   defaultValue:    x.Attr.DefaultValue))
               .ToList();
}
