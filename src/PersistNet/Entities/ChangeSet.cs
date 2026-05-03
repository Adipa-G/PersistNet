using System.Collections.Generic;
using PersistNet.Entities.VirtualDb;

namespace PersistNet.Entities;

internal sealed record PendingOperation(
    OperationType Type,
    string TableName,
    string? Schema,
    VRow Row);

internal sealed class ChangeSet
{
    private readonly List<PendingOperation> _operations = new();
    private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<PendingOperation> Operations => _operations;

    internal void Add(PendingOperation operation) => _operations.Add(operation);

    internal bool IsVisited(object entity) => _visited.Contains(entity);

    internal void MarkVisited(object entity) => _visited.Add(entity);
}
