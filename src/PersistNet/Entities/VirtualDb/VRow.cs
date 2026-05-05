using System;
using System.Collections.Generic;

namespace PersistNet.Entities.VirtualDb;

internal class VRow
{
    public OperationType OperationType { get; }
    public List<VCell> Cells { get; } = new List<VCell>();

    /// <summary>
    /// When set, called after the INSERT executes with the DB-generated key value.
    /// Only populated for entities with an auto-increment primary key.
    /// </summary>
    public Action<object?>? OnKeyGenerated { get; set; }

    public VRow(OperationType operationType)
    {
        OperationType = operationType;
    }
}