using System;
using System.Collections.Generic;
using System.Linq;

namespace PersistNet.Schema;

internal static class SchemaDiffer
{
    /// <summary>
    /// Computes the changes needed to bring <paramref name="actual"/> in line with
    /// <paramref name="desired"/>.
    /// </summary>
    public static SchemaDiff Compute(SchemaSnapshot desired, SchemaSnapshot actual)
    {
        var actualByKey = actual.Tables.ToDictionary(TableKey, StringComparer.OrdinalIgnoreCase);
        var desiredByKey = desired.Tables.ToDictionary(TableKey, StringComparer.OrdinalIgnoreCase);

        var tablesToCreate = new List<SchemaTable>();
        var tablesToDrop = new List<(string, string?)>();
        var columnsToAdd = new List<(string, string?, SchemaColumn)>();
        var columnsToAlter = new List<(string, string?, SchemaColumn)>();
        var columnsToDrop = new List<(string, string?, string)>();
        var indexesToCreate = new List<(string, string?, SchemaIndex)>();
        var indexesToDrop = new List<(string, string?, string)>();
        var fksToAdd = new List<(string, string?, SchemaForeignKey)>();
        var fksToDrop = new List<(string, string?, string)>();

        foreach (var (key, desiredTable) in desiredByKey)
        {
            if (!actualByKey.TryGetValue(key, out var actualTable))
            {
                tablesToCreate.Add(desiredTable);
                continue;
            }

            DiffColumns(desiredTable, actualTable, columnsToAdd, columnsToAlter, columnsToDrop);
            DiffIndexes(desiredTable, actualTable, indexesToCreate, indexesToDrop);
            DiffForeignKeys(desiredTable, actualTable, fksToAdd, fksToDrop);
        }

        foreach (var (key, actualTable) in actualByKey)
        {
            if (!desiredByKey.ContainsKey(key))
                tablesToDrop.Add((actualTable.Name, actualTable.Schema));
        }

        return new SchemaDiff(
            tablesToCreate,
            tablesToDrop,
            columnsToAdd,
            columnsToAlter,
            columnsToDrop,
            indexesToCreate,
            indexesToDrop,
            fksToAdd,
            fksToDrop);
    }

    private static void DiffColumns(
        SchemaTable desired,
        SchemaTable actual,
        List<(string, string?, SchemaColumn)> toAdd,
        List<(string, string?, SchemaColumn)> toAlter,
        List<(string, string?, string)> toDrop)
    {
        var actualByName = actual.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var desiredByName = desired.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, col) in desiredByName)
        {
            if (!actualByName.TryGetValue(name, out var actualCol))
                toAdd.Add((desired.Name, desired.Schema, col));
            else if (!ColumnsMatch(col, actualCol))
                toAlter.Add((desired.Name, desired.Schema, col));
        }

        foreach (var name in actualByName.Keys)
        {
            if (!desiredByName.ContainsKey(name))
                toDrop.Add((desired.Name, desired.Schema, name));
        }
    }

    private static void DiffIndexes(
        SchemaTable desired,
        SchemaTable actual,
        List<(string, string?, SchemaIndex)> toCreate,
        List<(string, string?, string)> toDrop)
    {
        var actualByName = actual.Indexes
            .Where(i => i.Name != null)
            .ToDictionary(i => i.Name!, StringComparer.OrdinalIgnoreCase);

        var desiredByName = desired.Indexes
            .Where(i => i.Name != null)
            .ToDictionary(i => i.Name!, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, idx) in desiredByName)
        {
            if (!actualByName.ContainsKey(name))
                toCreate.Add((desired.Name, desired.Schema, idx));
        }

        foreach (var name in actualByName.Keys)
        {
            if (!desiredByName.ContainsKey(name))
                toDrop.Add((desired.Name, desired.Schema, name));
        }
    }

    private static void DiffForeignKeys(
        SchemaTable desired,
        SchemaTable actual,
        List<(string, string?, SchemaForeignKey)> toAdd,
        List<(string, string?, string)> toDrop)
    {
        var actualByName = actual.ForeignKeys
            .Where(f => f.Name != null)
            .ToDictionary(f => f.Name!, StringComparer.OrdinalIgnoreCase);

        var desiredByName = desired.ForeignKeys
            .Where(f => f.Name != null)
            .ToDictionary(f => f.Name!, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, fk) in desiredByName)
        {
            if (!actualByName.ContainsKey(name))
                toAdd.Add((desired.Name, desired.Schema, fk));
        }

        foreach (var name in actualByName.Keys)
        {
            if (!desiredByName.ContainsKey(name))
                toDrop.Add((desired.Name, desired.Schema, name));
        }
    }

    private static bool ColumnsMatch(SchemaColumn desired, SchemaColumn actual) =>
        string.Equals(desired.DbType, actual.DbType, StringComparison.OrdinalIgnoreCase) &&
        desired.IsNullable == actual.IsNullable &&
        desired.IsAutoIncrement == actual.IsAutoIncrement &&
        desired.Size == actual.Size &&
        desired.Precision == actual.Precision &&
        desired.Scale == actual.Scale;

    private static string TableKey(SchemaTable t) =>
        t.Schema is not null ? $"{t.Schema}.{t.Name}" : t.Name;
}
