using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using PersistNet.DbInfo;
using PersistNet.Entities.VirtualDb;

namespace PersistNet.Entities;

internal sealed class ChangeSetBuilder
{
    private readonly ChangeSet _changeSet = new();

    /// <summary>
    /// Snapshots of column values captured at <c>GetAsync</c> time, keyed by object
    /// reference.  Used to detect dirty columns and suppress no-op UPDATEs.
    /// </summary>
    private readonly Dictionary<object, Dictionary<string, object?>> _snapshots =
        new(ReferenceEqualityComparer.Instance);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enqueues a save (INSERT or UPDATE) for <paramref name="entity"/> and every
    /// entity reachable through its navigation properties.
    /// </summary>
    public void Save(object entity) => EnqueueSave(entity);

    /// <summary>
    /// Enqueues a delete for <paramref name="entity"/> and every entity reachable
    /// through its navigation properties that holds a FK back to it.
    /// </summary>
    public void Delete(object entity) => EnqueueDelete(entity);

    /// <summary>
    /// Exposes the raw, unordered list of pending operations.  Primarily intended
    /// for inspection in tests; use <see cref="GetOrderedBatches"/> at commit time.
    /// </summary>
    public IReadOnlyList<PendingOperation> PendingOperations => _changeSet.Operations;

    /// <summary>
    /// Records a snapshot of <paramref name="entity"/>'s current column values.
    /// Called by <see cref="Transaction.GetAsync{T}"/> so that a subsequent
    /// <see cref="Save"/> can suppress unchanged columns from the UPDATE SET clause.
    /// </summary>
    internal void TrackSnapshot(object entity)
    {
        var table = DbInfoCache.FindTable(entity.GetType());
        if (table is null) return;

        var subType = DbInfoCache.FindSubType(table, entity.GetType());
        IEnumerable<Column> allColumns = subType is not null
            ? table.Columns.Concat(subType.ExtraColumns)
            : table.BaseTable is not null
                ? table.BaseTable.Columns.Concat(table.Columns)
                : table.Columns;

        _snapshots[entity] = allColumns
            .GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First().Getter(entity),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies a topological sort over the accumulated change set and collapses
    /// consecutive same-(table, operation) runs into <see cref="VTable"/> batches,
    /// enabling per-table query optimisations at commit time.
    /// </summary>
    public IReadOnlyList<VTable> GetOrderedBatches() => SortAndBatch(_changeSet);

    // -------------------------------------------------------------------------
    // Save traversal
    // -------------------------------------------------------------------------

    private void EnqueueSave(object entity)
    {
        if (_changeSet.IsVisited(entity)) return;
        _changeSet.MarkVisited(entity);

        var table = RequireTable(entity.GetType());

        // 1. M2O parents must exist before this row's FK can reference them.
        foreach (var rel in table.Relationships.OfType<ManyToOneRelationship>())
        {
            var parent = rel.Getter(entity);
            if (parent != null) EnqueueSave(parent);
        }

        // 2. O2O owning side (MappedBy == null, has FromKeys) — same FK ownership rule.
        foreach (var rel in table.Relationships.OfType<OneToOneRelationship>()
            .Where(r => r.MappedBy == null))
        {
            var related = rel.Getter(entity);
            if (related != null) EnqueueSave(related);
        }

        // 3. Map this entity and enqueue.
        var op = IsInsert(entity, table) ? OperationType.Insert : OperationType.Update;
        _snapshots.TryGetValue(entity, out var snapshot);

        if (table.BaseTable != null)
        {
            // Joined subtype: entity data is split across its base table and its own table.
            EnqueueSaveJoinedSubtype(entity, table, op, snapshot);
        }
        else
        {
            var row = MapToRow(entity, table, op, snapshot);

            // Dirty tracking: if nothing in the SET clause changed, skip the UPDATE.
            if (op == OperationType.Update)
            {
                var keyOrVersionColNames = table.Columns
                    .Where(c => c.IsKey || c.IsVersion)
                    .Select(c => c.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (row.Cells.All(c => keyOrVersionColNames.Contains(c.ColumnName)))
                    goto AfterEnqueue;
            }

            _changeSet.Add(new PendingOperation(op, table.Name, table.Schema, row));
        }
        AfterEnqueue:

        // 4. O2M children reference this entity's PK — they are saved after us.
        foreach (var rel in table.Relationships.OfType<OneToManyRelationship>())
        {
            if (rel.Getter(entity) is not IEnumerable<object> children) continue;
            foreach (var child in children)
                if (child != null) EnqueueSave(child);
        }

        // 5. O2O inverse side — the other entity holds the FK to us; save it after us.
        foreach (var rel in table.Relationships.OfType<OneToOneRelationship>()
            .Where(r => r.MappedBy != null))
        {
            var related = rel.Getter(entity);
            if (related != null) EnqueueSave(related);
        }

        // 6. M2M owning side — save the related entity first, then enqueue the join row.
        foreach (var rel in table.Relationships.OfType<ManyToManyRelationship>()
            .Where(r => r.MappedBy == null))
        {
            if (rel.RelatedType == null) continue;
            var rightTable = DbInfoCache.FindTable(rel.RelatedType);
            if (rightTable == null) continue;
            if (rel.Getter(entity) is not IEnumerable<object> related) continue;

            var joinName = rel.JoinTableName ?? $"{table.Name}_{rightTable.Name}";

            foreach (var right in related)
            {
                if (right == null) continue;
                EnqueueSave(right);

                // Join row is always added even if the right entity was already visited,
                // because each distinct (left, right) pair needs its own join-table row.
                var joinRow = BuildJoinRow(entity, table, right, rightTable, rel, OperationType.Insert);
                _changeSet.Add(new PendingOperation(OperationType.Insert, joinName, rel.JoinTableSchema, joinRow));
            }
        }
    }

    /// <summary>
    /// Handles saving a joined-subtype entity: emits separate INSERT/UPDATE operations
    /// for the base table and the subtype's own table.
    /// <para>
    /// INSERT: The base row carries the <c>OnKeyGenerated</c> callback.  When the
    /// base INSERT fires and returns the DB-generated Id, the callback both sets
    /// <c>entity.Id</c> and injects the Id cell into the subtype row
    /// before that INSERT is optimised and executed.
    /// </para>
    /// </summary>
    private void EnqueueSaveJoinedSubtype(object entity, Table table, OperationType op,
        Dictionary<string, object?>? snapshot)
    {
        var baseTable = table.BaseTable!;

        if (op == OperationType.Insert)
        {
            var baseRow = MapToRow(entity, baseTable, OperationType.Insert, null);

            // Build the join row without the PK cell — it will be injected by the
            // base INSERT's OnKeyGenerated callback (or directly below for non-AutoIncrement).
            var joinRow = new VRow(OperationType.Insert);
            foreach (var col in table.Columns.Where(c => !c.IsKey))
                joinRow.Cells.Add(new VCell(col.ColumnName, col.Getter(entity)));

            if (baseTable.Columns.Any(c => c.IsKey && c.AutoIncrement))
            {
                // Extend the base row callback to also inject the generated Id into the
                // join row's cells.  OptimizeInsert is called lazily (after this fires),
                // so joinRow.Cells will contain the correct Id when the join INSERT runs.
                var originalCallback = baseRow.OnKeyGenerated;
                var pkCol = table.Columns.First(c => c.IsKey);
                baseRow.OnKeyGenerated = generatedId =>
                {
                    originalCallback?.Invoke(generatedId); // sets entity.Id
                    var converted = Convert.ChangeType(generatedId, pkCol.Property.PropertyType);
                    joinRow.Cells.Insert(0, new VCell(pkCol.ColumnName, converted));
                };
            }
            else
            {
                // No AutoIncrement — PK is already known; include all key columns in the join row.
                var pkCells = table.Columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrder)
                    .Select(pkCol => new VCell(pkCol.ColumnName, pkCol.Getter(entity)));
                joinRow.Cells.InsertRange(0, pkCells);
            }

            _changeSet.Add(new PendingOperation(OperationType.Insert, baseTable.Name, baseTable.Schema, baseRow));
            _changeSet.Add(new PendingOperation(OperationType.Insert, table.Name, table.Schema, joinRow));
        }
        else // UPDATE
        {
            // Base table: dirty-check base columns and skip UPDATE if nothing changed.
            var baseRow = MapToRow(entity, baseTable, OperationType.Update, snapshot);
            var baseKeyOrVersionCols = baseTable.Columns
                .Where(c => c.IsKey || c.IsVersion)
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!baseRow.Cells.All(c => baseKeyOrVersionCols.Contains(c.ColumnName)))
                _changeSet.Add(new PendingOperation(OperationType.Update, baseTable.Name, baseTable.Schema, baseRow));

            // Join table: dirty-check own columns and skip UPDATE if nothing changed.
            var joinRow = MapToRow(entity, table, OperationType.Update, snapshot);
            var joinKeyOrVersionCols = table.Columns
                .Where(c => c.IsKey || c.IsVersion)
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!joinRow.Cells.All(c => joinKeyOrVersionCols.Contains(c.ColumnName)))
                _changeSet.Add(new PendingOperation(OperationType.Update, table.Name, table.Schema, joinRow));
        }
    }

    // -------------------------------------------------------------------------
    // Delete traversal
    // -------------------------------------------------------------------------

    private void EnqueueDelete(object entity)
    {
        if (_changeSet.IsVisited(entity)) return;
        _changeSet.MarkVisited(entity);

        var table = RequireTable(entity.GetType());

        // 1. Join-table rows reference both sides — delete them before either entity.
        foreach (var rel in table.Relationships.OfType<ManyToManyRelationship>()
            .Where(r => r.MappedBy == null))
        {
            if (rel.RelatedType == null) continue;
            var rightTable = DbInfoCache.FindTable(rel.RelatedType);
            if (rightTable == null) continue;
            if (rel.Getter(entity) is not IEnumerable<object> related) continue;

            var joinName = rel.JoinTableName ?? $"{table.Name}_{rightTable.Name}";

            foreach (var right in related)
            {
                if (right == null) continue;
                var joinRow = BuildJoinRow(entity, table, right, rightTable, rel, OperationType.Delete);
                _changeSet.Add(new PendingOperation(OperationType.Delete, joinName, rel.JoinTableSchema, joinRow));
            }
        }

        // 2. O2M children have a FK to this entity — delete them before us.
        foreach (var rel in table.Relationships.OfType<OneToManyRelationship>())
        {
            if (rel.Getter(entity) is not IEnumerable<object> children) continue;
            foreach (var child in children)
                if (child != null) EnqueueDelete(child);
        }

        // 3. O2O inverse side holds a FK to us — delete it before us.
        foreach (var rel in table.Relationships.OfType<OneToOneRelationship>()
            .Where(r => r.MappedBy != null))
        {
            var related = rel.Getter(entity);
            if (related != null) EnqueueDelete(related);
        }

        // 4. Delete this entity.
        if (table.BaseTable != null)
        {
            // Joined subtype: delete the subtype row first, then the base row.
            // Topological sort (OrderByDescending) enforces this ordering at commit time.
            _changeSet.Add(new PendingOperation(
                OperationType.Delete, table.Name, table.Schema,
                MapToRow(entity, table, OperationType.Delete)));
            _changeSet.Add(new PendingOperation(
                OperationType.Delete, table.BaseTable.Name, table.BaseTable.Schema,
                MapToRow(entity, table.BaseTable, OperationType.Delete)));
        }
        else
        {
            _changeSet.Add(new PendingOperation(
                OperationType.Delete, table.Name, table.Schema,
                MapToRow(entity, table, OperationType.Delete)));
        }
    }

    // -------------------------------------------------------------------------
    // Commit-time ordering — topological sort + batch collapsing
    // -------------------------------------------------------------------------

    private static IReadOnlyList<VTable> SortAndBatch(ChangeSet cs)
    {
        if (cs.Operations.Count == 0)
            return Array.Empty<VTable>();

        // Build dependency graph: deps[A] = set of table names A depends on (A has FK → them).
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in cs.Operations)
            deps.TryAdd(op.TableName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        foreach (var db in DbInfoCache.GetAllCached())
        {
            foreach (var table in db.Tables)
            {
                // Regular FK edges from entity tables.
                if (deps.ContainsKey(table.Name))
                {
                    foreach (var rel in table.Relationships)
                    {
                        var relatedName = rel switch
                        {
                            ManyToOneRelationship m2o when m2o.RelatedType != null
                                => DbInfoCache.FindTable(m2o.RelatedType)?.Name,
                            OneToOneRelationship o2o when o2o.MappedBy == null && o2o.RelatedType != null
                                => DbInfoCache.FindTable(o2o.RelatedType)?.Name,
                            _ => null
                        };

                        if (relatedName != null
                            && deps.ContainsKey(relatedName)
                            && !table.Name.Equals(relatedName, StringComparison.OrdinalIgnoreCase))
                        {
                            deps[table.Name].Add(relatedName);
                        }
                    }
                }

                // Join-table edges: join table depends on both referenced entity tables.
                foreach (var m2m in table.Relationships.OfType<ManyToManyRelationship>()
                    .Where(r => r.MappedBy == null && r.RelatedType != null))
                {
                    var rightTable = DbInfoCache.FindTable(m2m.RelatedType!);
                    if (rightTable == null) continue;

                    var joinName = m2m.JoinTableName ?? $"{table.Name}_{rightTable.Name}";
                    if (!deps.ContainsKey(joinName)) continue;

                    if (deps.ContainsKey(table.Name))
                        deps[joinName].Add(table.Name);
                    if (deps.ContainsKey(rightTable.Name))
                        deps[joinName].Add(rightTable.Name);
                }

                // Joined-subtype edges: the subtype table depends on its base table.
                if (table.BaseTable != null
                    && deps.ContainsKey(table.Name)
                    && deps.ContainsKey(table.BaseTable.Name))
                {
                    deps[table.Name].Add(table.BaseTable.Name);
                }
            }
        }

        // Kahn's topological sort.
        var inDegree = deps.Keys.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
        var successors = deps.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var (node, prereqs) in deps)
            foreach (var pre in prereqs)
            {
                successors[pre].Add(node);
                inDegree[node]++;
            }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var insertOrder = new List<string>(deps.Count);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            insertOrder.Add(current);
            foreach (var s in successors[current])
                if (--inDegree[s] == 0) queue.Enqueue(s);
        }

        if (insertOrder.Count < deps.Count)
            throw new InvalidOperationException(
                "A circular foreign key dependency was detected in the change set. " +
                "Self-referential tables with non-nullable foreign keys cannot be ordered automatically.");

        var orderMap = insertOrder
            .Select((name, idx) => (name, idx))
            .ToDictionary(x => x.name, x => x.idx, StringComparer.OrdinalIgnoreCase);

        int Order(PendingOperation op) =>
            orderMap.TryGetValue(op.TableName, out var i) ? i : int.MaxValue;

        // Sort: inserts → updates → deletes (deletes in reverse topo order).
        var inserts = cs.Operations.Where(o => o.Type == OperationType.Insert).OrderBy(Order);
        var updates = cs.Operations.Where(o => o.Type == OperationType.Update).OrderBy(Order);
        var deletes = cs.Operations.Where(o => o.Type == OperationType.Delete).OrderByDescending(Order);

        // Collapse consecutive same-(table, op) runs into VTable batches.
        var batches = new List<VTable>();
        string? batchTable = null;
        OperationType? batchOp = null;
        string? batchSchema = null;
        List<VRow>? batchRows = null;

        foreach (var op in inserts.Concat(updates).Concat(deletes))
        {
            if (batchTable == null
                || !batchTable.Equals(op.TableName, StringComparison.OrdinalIgnoreCase)
                || batchOp != op.Type)
            {
                if (batchTable != null)
                    batches.Add(new VTable(batchTable, batchSchema, batchOp!.Value, batchRows!));

                batchTable = op.TableName;
                batchSchema = op.Schema;
                batchOp = op.Type;
                batchRows = new List<VRow> { op.Row };
            }
            else
            {
                batchRows!.Add(op.Row);
            }
        }

        if (batchTable != null)
            batches.Add(new VTable(batchTable, batchSchema, batchOp!.Value, batchRows!));

        return batches;
    }

    // -------------------------------------------------------------------------
    // Mapping helpers
    // -------------------------------------------------------------------------

    private static bool IsInsert(object entity, Table table)
    {
        foreach (var col in table.Columns.Where(c => c.IsKey))
        {
            var value = col.Getter(entity);
            var defaultValue = col.Property.PropertyType.IsValueType
                ? Activator.CreateInstance(col.Property.PropertyType)
                : null;

            if (!Equals(value, defaultValue))
                return false; // at least one key is set → UPDATE
        }
        return true; // all keys are null/default → INSERT
    }

    private static VRow MapToRow(object entity, Table table, OperationType op,
        Dictionary<string, object?>? snapshot = null)
    {
        var row = new VRow(op);
        var subType = DbInfoCache.FindSubType(table, entity.GetType());

        if (op == OperationType.Delete)
        {
            foreach (var col in table.Columns.Where(c => c.IsKey))
                row.Cells.Add(new VCell(col.ColumnName, col.Getter(entity)));
            return row;
        }

        // For INSERT with an auto-increment PK: skip the PK column (DB generates it)
        // and register a callback to write the generated value back to the entity.
        if (op == OperationType.Insert)
        {
            var autoIncKey = table.Columns.FirstOrDefault(c => c.IsKey && c.AutoIncrement);
            if (autoIncKey is not null)
            {
                var keyCol     = autoIncKey;
                var entityRef  = entity;
                row.OnKeyGenerated = generatedId =>
                {
                    var converted = Convert.ChangeType(generatedId, keyCol.Property.PropertyType);
                    keyCol.Property.SetValue(entityRef, converted);
                };
            }
        }

        foreach (var col in table.Columns)
        {
            // Skip auto-increment PK on INSERT — the DB generates the value.
            if (op == OperationType.Insert && col.IsKey && col.AutoIncrement)
                continue;

            var value = col.IsDiscriminator && subType != null
                ? subType.DiscriminatorValue
                : col.Getter(entity);

            // Dirty tracking: skip non-key, non-version columns on UPDATE when the
            // value is identical to the snapshot captured at GetAsync time.
            if (op == OperationType.Update
                && !col.IsKey && !col.IsVersion
                && snapshot is not null
                && snapshot.TryGetValue(col.ColumnName, out var original)
                && Equals(value, original))
                continue;

            var cell = (op == OperationType.Update && col.IsVersion)
                ? new VCell(col.ColumnName, value) { IsVersion = true }
                : new VCell(col.ColumnName, value);

            row.Cells.Add(cell);
        }

        if (subType != null)
            foreach (var col in subType.ExtraColumns)
            {
                var value = col.Getter(entity);
                // Dirty tracking applies to subtype extra columns too.
                if (op == OperationType.Update
                    && !col.IsVersion
                    && snapshot is not null
                    && snapshot.TryGetValue(col.ColumnName, out var original)
                    && Equals(value, original))
                    continue;
                row.Cells.Add(new VCell(col.ColumnName, value));
            }

        return row;
    }

    private static VRow BuildJoinRow(
        object leftEntity, Table leftTable,
        object rightEntity, Table rightTable,
        ManyToManyRelationship m2m,
        OperationType op)
    {
        var row = new VRow(op);

        // LeftKeyColumns[i]  = column name in the join table
        // LeftForeignKeys[i] = property name on the left entity that provides the value
        for (var i = 0; i < m2m.LeftKeyColumns.Length; i++)
        {
            var srcName = m2m.LeftForeignKeys.Length > i ? m2m.LeftForeignKeys[i] : null;
            var col = srcName != null
                ? leftTable.Columns.First(c => c.Property.Name == srcName || c.ColumnName == srcName)
                : leftTable.Columns.First(c => c.IsKey);
            row.Cells.Add(new VCell(m2m.LeftKeyColumns[i], col.Getter(leftEntity)));
        }

        for (var i = 0; i < m2m.RightKeyColumns.Length; i++)
        {
            var srcName = m2m.RightForeignKeys.Length > i ? m2m.RightForeignKeys[i] : null;
            var col = srcName != null
                ? rightTable.Columns.First(c => c.Property.Name == srcName || c.ColumnName == srcName)
                : rightTable.Columns.First(c => c.IsKey);
            row.Cells.Add(new VCell(m2m.RightKeyColumns[i], col.Getter(rightEntity)));
        }

        return row;
    }

    private static Table RequireTable(Type type)
        => DbInfoCache.FindTable(type)
           ?? throw new InvalidOperationException(
               $"Type '{type.Name}' is not a registered entity. Ensure it is annotated with [TableInfo].");
}
