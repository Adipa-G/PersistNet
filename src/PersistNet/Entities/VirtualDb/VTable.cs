using System.Collections.Generic;

namespace PersistNet.Entities.VirtualDb;

internal sealed record VTable(
    string TableName,
    string? Schema,
    OperationType OperationType,
    IReadOnlyList<VRow> Rows);
