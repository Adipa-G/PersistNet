using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PersistNet.DbInfo;

internal static class DbInfoCache
{
    private static readonly ConcurrentDictionary<Assembly, Database> _cache = new();

    public static Database GetOrExtract(Assembly assembly)
        => _cache.GetOrAdd(assembly, DbInfoExtractor.Extract);

    public static IEnumerable<Database> GetAllCached() => _cache.Values;

    /// <summary>
    /// Returns the root <see cref="Table"/> for the given CLR type, whether the type is
    /// a root entity or a registered sub-type (STI).
    /// </summary>
    public static Table? FindTable(Type type)
    {
        var db = GetOrExtract(type.Assembly);
        return db.Tables.FirstOrDefault(t => t.EntityType == type)
            ?? db.Tables.FirstOrDefault(t => t.SubTypes.Any(s => s.EntityType == type));
    }

    /// <summary>
    /// Returns the first <see cref="Table"/> whose <see cref="Table.Name"/> matches
    /// <paramref name="tableName"/> (case-insensitive) across all cached databases.
    /// When <paramref name="schema"/> is supplied it is also matched case-insensitively.
    /// Returns <c>null</c> if no matching table has been cached yet.
    /// </summary>
    public static Table? FindTableByName(string tableName, string? schema = null)
    {
        foreach (var db in GetAllCached())
            foreach (var table in db.Tables)
                if (string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase)
                    && (schema is null || string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase)))
                    return table;
        return null;
    }

    /// <summary>
    /// Returns the <see cref="SubType"/> entry for <paramref name="type"/> within
    /// <paramref name="table"/>, or <c>null</c> when <paramref name="type"/> is the
    /// root entity of that table.
    /// </summary>
    public static SubType? FindSubType(Table table, Type type)
    {
        if (table.EntityType == type) return null;
        return table.SubTypes.FirstOrDefault(s => s.EntityType == type);
    }
}
