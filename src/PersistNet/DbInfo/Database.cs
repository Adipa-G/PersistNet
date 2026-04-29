using System.Collections.Generic;

namespace PersistNet.DbInfo;

internal sealed class Database
{
    public IReadOnlyList<Table> Tables { get; }

    internal Database(IReadOnlyList<Table> tables)
    {
        Tables = tables;
    }
}
