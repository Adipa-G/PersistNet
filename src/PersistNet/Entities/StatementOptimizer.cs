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
        // Derive a stable column order from the union of all row cells.
        // Using a union (rather than only the first row) is necessary for
        // Single-Table Inheritance: different subtype rows contribute different
        // extra columns, and all of them must appear in the INSERT statement.
        var columns = vtable.Rows
            .SelectMany(r => r.Cells.Select(c => c.ColumnName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
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

        // All rows in a batch share the same auto-increment key column (or none).
        var keyColName = vtable.Rows.FirstOrDefault(r => r.AutoIncrKeyColumn is not null)?.AutoIncrKeyColumn;

        return new[] { new MultiRowInsert(vtable.TableName, vtable.Schema, columns, valueRows,
            hasCallbacks ? callbacks : null, keyColName) };
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
        // Fingerprint is based on SET-clause values only; the version baseline is NOT
        // part of the fingerprint so that rows sharing identical data changes but at
        // different starting versions are merged into one group.
        var groups = new Dictionary<string, (
            List<SetClause> SetClauses,
            List<IReadOnlyList<object?>> KeyValues,
            string? VersionColumn,
            List<object?> ExpectedVersionValues)>(StringComparer.Ordinal);
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

            // Fingerprint is data values only — version baseline is excluded so that
            // entities at different starting versions collapse into the same group when
            // they carry the same data change.
            var fingerprint = string.Join("|", setCells.Select(
                c => $"{c.ColumnName}={(c.Value is null ? NullSentinel : c.Value)}"));

            if (!groups.TryGetValue(fingerprint, out var group))
            {
                var setClauses = setCells
                    .Select(c => new SetClause(c.ColumnName, c.Value))
                    .ToList();

                // Version column is NOT added to SetClauses here.  When all rows in
                // the group share the same expected version the homogeneous path emits
                // a fixed incremented value; when versions differ the mixed path emits
                // "ver = ver + 1" as a computed SQL expression instead.

                group = (setClauses, new List<IReadOnlyList<object?>>(),
                    versionCell?.ColumnName,
                    new List<object?>());
                groups[fingerprint] = group;
                groupOrder.Add(fingerprint);
            }

            var keyValues = new object?[keyColumns.Count];
            foreach (var cell in keyCells)
                if (keyIndex.TryGetValue(cell.ColumnName, out var idx))
                    keyValues[idx] = cell.Value;

            group.KeyValues.Add(keyValues);

            if (versionCell is not null)
                group.ExpectedVersionValues.Add(versionCell.Value);
        }

        return groupOrder
            .Select(fp =>
            {
                var (setClauses, keyValues, versionColumn, expectedVersionValues) = groups[fp];

                if (versionColumn is null)
                    return (OptimizedOperation)new GroupedUpdate(
                        vtable.TableName, vtable.Schema,
                        setClauses, keyColumns, keyValues);

                // Homogeneous: all rows share the same expected version.
                // Emit a fixed incremented value in SetClauses and use the efficient
                // Id IN (…) AND Version = @shared form.
                var firstVersion = (long)Convert.ChangeType(expectedVersionValues[0]!, typeof(long));
                var allSame = expectedVersionValues.All(v =>
                    (long)Convert.ChangeType(v!, typeof(long)) == firstVersion);

                if (allSame)
                {
                    var versionedSet = new List<SetClause>(setClauses)
                    {
                        new SetClause(versionColumn, (object)(firstVersion + 1))
                    };
                    return (OptimizedOperation)new GroupedUpdate(
                        vtable.TableName, vtable.Schema,
                        versionedSet, keyColumns, keyValues,
                        versionColumn, ExpectedVersionValue: expectedVersionValues[0]);
                }

                // Mixed: rows have different expected versions.
                // SetClauses does NOT include the version column; the SQL layer will
                // emit "ver = ver + 1" as a computed expression and use per-row
                // (key, version) WHERE predicates.
                return (OptimizedOperation)new GroupedUpdate(
                    vtable.TableName, vtable.Schema,
                    setClauses, keyColumns, keyValues,
                    versionColumn, ExpectedVersionValues: expectedVersionValues);
            })
            .ToList();
    }
}
