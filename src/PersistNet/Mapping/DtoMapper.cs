using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;

namespace PersistNet.Mapping;

/// <summary>
/// Maps <see cref="DbDataReader"/> rows to DTO/model types that carry
/// <see cref="ColumnInfo"/> attributes but no <see cref="TableInfo"/>.
/// Column metadata is cached per type via <see cref="DtoColumnCache"/>.
/// </summary>
internal static class DtoMapper
{
    /// <summary>
    /// Materializes the current reader row into a new instance of
    /// <typeparamref name="T"/>. Only properties decorated with
    /// <see cref="ColumnInfo"/> are populated; unrecognised result-set columns are
    /// silently ignored.
    /// </summary>
    internal static T Materialize<T>(DbDataReader reader) where T : class, new()
    {
        var instance = new T();
        var columns  = DtoColumnCache.GetOrExtract(typeof(T));

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var fieldName = reader.GetName(i);
            Column? col = null;
            foreach (var c in columns)
            {
                if (string.Equals(c.ColumnName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    col = c;
                    break;
                }
            }
            if (col is null) continue;

            var rawValue = reader.IsDBNull(i) ? null : reader.GetValue(i);
            col.Property.SetValue(instance, ValueConverter.Convert(rawValue, col.Property.PropertyType));
        }

        return instance;
    }

    /// <summary>
    /// Converts a caller-supplied parameters object into the internal
    /// <c>(Name, Value)</c> list used by <see cref="AnsiSqlPersistenceBase"/>.
    /// Accepted inputs:
    /// <list type="bullet">
    ///   <item><c>null</c> — no parameters (returns empty list)</item>
    ///   <item><see cref="IDictionary{TKey,TValue}"/> of string→object? — copied directly</item>
    ///   <item>Any other object — public readable properties are reflected; each becomes
    ///         <c>@PropertyName</c></item>
    /// </list>
    /// </summary>
    internal static List<(string Name, object? Value)> ExtractParameters(object? parameters)
    {
        if (parameters is null)
            return [];

        if (parameters is IDictionary<string, object?> dict)
        {
            var result = new List<(string, object?)>(dict.Count);
            foreach (var kv in dict)
                result.Add(($"@{kv.Key}", kv.Value));
            return result;
        }

        // Anonymous object or any POCO — reflect public readable properties.
        var props = parameters.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var list = new List<(string, object?)>(props.Length);
        foreach (var prop in props)
            list.Add(($"@{prop.Name}", prop.GetValue(parameters)));
        return list;
    }
}
