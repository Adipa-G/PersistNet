using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;
using PersistNet.Entities.VirtualDb;

namespace PersistNet.Entities;

internal sealed class ChangeSetBuilder
{
    private readonly ChangeSet _changeSet = new();

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
        _changeSet.Add(new PendingOperation(op, table.Name, table.Schema, MapToRow(entity, table, op)));

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

        // 4. Delete this entity (key columns only — the WHERE clause).
        _changeSet.Add(new PendingOperation(
            OperationType.Delete, table.Name, table.Schema,
            MapToRow(entity, table, OperationType.Delete)));
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

    private static VRow MapToRow(object entity, Table table, OperationType op)
    {
        var row = new VRow(op);
        var subType = DbInfoCache.FindSubType(table, entity.GetType());

        if (op == OperationType.Delete)
        {
            foreach (var col in table.Columns.Where(c => c.IsKey))
                row.Cells.Add(new VCell(col.ColumnName, col.Getter(entity)));
            return row;
        }

        foreach (var col in table.Columns)
        {
            var value = col.IsDiscriminator && subType != null
                ? subType.DiscriminatorValue
                : col.Getter(entity);

            row.Cells.Add(new VCell(col.ColumnName, value));
        }

        if (subType != null)
            foreach (var col in subType.ExtraColumns)
                row.Cells.Add(new VCell(col.ColumnName, col.Getter(entity)));

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
