using System.Collections.Generic;

namespace PersistNet.Entities.VirtualDb;

internal class VRow
{
    public OperationType OperationType { get; }
    public List<VCell> Cells { get; } = new List<VCell>();

    public VRow(OperationType operationType)
    {
        OperationType = operationType;
    }
}