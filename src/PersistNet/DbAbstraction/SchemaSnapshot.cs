using System.Collections.Generic;

namespace PersistNet.DbAbstraction;

internal sealed class SchemaSnapshot
{
    public IReadOnlyList<SchemaTable> Tables { get; }

    internal SchemaSnapshot(IReadOnlyList<SchemaTable> tables)
    {
        Tables = tables;
    }
}
