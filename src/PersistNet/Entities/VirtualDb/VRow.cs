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

    /// <summary>
    /// Name of the auto-increment primary-key column, populated alongside
    /// <see cref="OnKeyGenerated"/>.  Providers that support <c>OUTPUT INSERTED</c>
    /// (SQL Server) use this to hydrate keys in a single batch round trip.
    /// </summary>
    public string? AutoIncrKeyColumn { get; set; }

    public VRow(OperationType operationType)
    {
        OperationType = operationType;
    }
}