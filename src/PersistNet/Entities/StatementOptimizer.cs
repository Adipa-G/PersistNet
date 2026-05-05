using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;
using PersistNet.Entities.VirtualDb;

namespace PersistNet.Entities;

/// <summary>
/// Collapses the rows in a <see cref="VTable"/> into the minimum number of
/// provider-agnostic <see cref="OptimizedOperation"/> instances.
///
/// Rules:
///   INSERT  → one <see cref="MultiRowInsert"/>  (all rows merged)
///   DELETE  → one <see cref="BatchDelete"/>      (all key values merged)
///   UPDATE  → one <see cref="GroupedUpdate"/> per distinct SET-clause fingerprint
///             (rows whose non-key columns share identical values are merged)
/// </summary>
internal static class StatementOptimizer
{
    /// <summary>
    /// Sentinel used when a cell value is <c>null</c> inside a fingerprint string,
    /// so that <c>null</c> and <c>""</c> produce different fingerprints.
    /// </summary>
    private const string NullSentinel = "\x00<NULL>\x00";

    public static IReadOnlyList<OptimizedOperation> Optimize(VTable vtable)
    {
        if (vtable.Rows.Count == 0)
            return Array.Empty<OptimizedOperation>();

        return vtable.OperationType switch
        {
            OperationType.Insert => OptimizeInsert(vtable),
            OperationType.Delete => OptimizeDelete(vtable),
            OperationType.Update => OptimizeUpdate(vtable),
            _ => throw new ArgumentOutOfRangeException(nameof(vtable),
                     $"Unknown OperationType '{vtable.OperationType}'.")
        };
    }

    // -------------------------------------------------------------------------
    // INSERT — all rows collapse into one multi-row operation
    // -------------------------------------------------------------------------

    private static IReadOnlyList<OptimizedOperation> OptimizeInsert(VTable vtable)
    {
        // Derive a stable column order from the first row.
        var columns = vtable.Rows[0].Cells
            .OrderBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.ColumnName)
            .ToList();

        var columnIndex = columns
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        var valueRows = vtable.Rows.Select(row =>
        {
            var values = new object?[columns.Count];
            foreach (var cell in row.Cells)
                if (columnIndex.TryGetValue(cell.ColumnName, out var idx))
                    values[idx] = cell.Value;
            return (IReadOnlyList<object?>)values;
        }).ToList();

        // Propagate any key-hydration callbacks from VRows.
        var callbacks = vtable.Rows.Select(r => r.OnKeyGenerated).ToList();
        var hasCallbacks = callbacks.Any(c => c is not null);

        return new[] { new MultiRowInsert(vtable.TableName, vtable.Schema, columns, valueRows,
            hasCallbacks ? callbacks : null) };
    }

    // -------------------------------------------------------------------------
    // DELETE — all rows collapse into one batch (all cells are key cells)
    // -------------------------------------------------------------------------

    private static IReadOnlyList<OptimizedOperation> OptimizeDelete(VTable vtable)
    {
        // MapToRow for DELETE emits key columns only, in the order they appear on
        // the entity.  Normalise to alphabetical order for a stable, predictable layout.
        var keyColumns = vtable.Rows[0].Cells
            .OrderBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.ColumnName)
            .ToList();

        var columnIndex = keyColumns
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        var keyValues = vtable.Rows.Select(row =>
        {
            var values = new object?[keyColumns.Count];
            foreach (var cell in row.Cells)
                if (columnIndex.TryGetValue(cell.ColumnName, out var idx))
                    values[idx] = cell.Value;
            return (IReadOnlyList<object?>)values;
        }).ToList();

        return new[] { new BatchDelete(vtable.TableName, vtable.Schema, keyColumns, keyValues) };
    }

    // -------------------------------------------------------------------------
    // UPDATE — group rows by identical SET-clause fingerprint
    // -------------------------------------------------------------------------

    private static IReadOnlyList<OptimizedOperation> OptimizeUpdate(VTable vtable)
    {
        var table = DbInfoCache.FindTableByName(vtable.TableName, vtable.Schema)
            ?? throw new InvalidOperationException(
                $"Cannot optimise UPDATE for table '{vtable.TableName}': the table was not " +
                "found in the metadata cache. Ensure the entity type's assembly has been " +
                "registered with DbInfoCache before calling StatementOptimizer.");

        var keyColNames = table.Columns
            .Where(c => c.IsKey)
            .Select(c => c.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Stable key column order (alphabetical).
        var keyColumns = keyColNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var keyIndex = keyColumns
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        // Group by fingerprint — preserving insertion order of groups so that the
        // output order is deterministic (important for tests and for SQL ordering).
        var groups = new Dictionary<string, (
            List<SetClause> SetClauses,
            List<IReadOnlyList<object?>> KeyValues,
            string? VersionColumn,
            object? ExpectedVersionValue)>(StringComparer.Ordinal);
        var groupOrder = new List<string>();

        foreach (var row in vtable.Rows)
        {
            var versionCell = row.Cells.FirstOrDefault(c => c.IsVersion);

            var setCells = row.Cells
                .Where(c => !keyColNames.Contains(c.ColumnName) && !c.IsVersion)
                .OrderBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keyCells = row.Cells
                .Where(c => keyColNames.Contains(c.ColumnName))
                .ToList();

            // Fingerprint includes regular SET values + version new-value so that
            // rows sharing the same old version group together.
            long newVersionValue = versionCell is not null
                ? (long)Convert.ChangeType(versionCell.Value!, typeof(long)) + 1
                : 0;

            var fingerprint = string.Join("|", setCells.Select(
                c => $"{c.ColumnName}={(c.Value is null ? NullSentinel : c.Value)}"))
                + (versionCell is not null ? $"|__ver={newVersionValue}" : "");

            if (!groups.TryGetValue(fingerprint, out var group))
            {
                var setClauses = setCells
                    .Select(c => new SetClause(c.ColumnName, c.Value))
                    .ToList();

                // Version column goes into SetClauses with the incremented value.
                if (versionCell is not null)
                    setClauses.Add(new SetClause(versionCell.ColumnName, (object)newVersionValue));

                group = (setClauses, new List<IReadOnlyList<object?>>(),
                    versionCell?.ColumnName,
                    versionCell?.Value);
                groups[fingerprint] = group;
                groupOrder.Add(fingerprint);
            }

            var keyValues = new object?[keyColumns.Count];
            foreach (var cell in keyCells)
                if (keyIndex.TryGetValue(cell.ColumnName, out var idx))
                    keyValues[idx] = cell.Value;

            group.KeyValues.Add(keyValues);
        }

        return groupOrder
            .Select(fp =>
            {
                var (setClauses, keyValues, versionColumn, expectedVersion) = groups[fp];
                return (OptimizedOperation)new GroupedUpdate(
                    vtable.TableName, vtable.Schema,
                    setClauses, keyColumns, keyValues,
                    versionColumn, expectedVersion);
            })
            .ToList();
    }
}
