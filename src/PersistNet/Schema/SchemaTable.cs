using System.Collections.Generic;

namespace PersistNet.Schema;

internal sealed class SchemaTable
{
    public string Name { get; }
    public string? Schema { get; }
    public SchemaPrimaryKey? PrimaryKey { get; }
    public IReadOnlyList<SchemaColumn> Columns { get; }
    public IReadOnlyList<SchemaIndex> Indexes { get; }
    public IReadOnlyList<SchemaForeignKey> ForeignKeys { get; }

    internal SchemaTable(
        string name,
        string? schema,
        SchemaPrimaryKey? primaryKey,
        IReadOnlyList<SchemaColumn> columns,
        IReadOnlyList<SchemaIndex> indexes,
        IReadOnlyList<SchemaForeignKey> foreignKeys)
    {
        Name = name;
        Schema = schema;
        PrimaryKey = primaryKey;
        Columns = columns;
        Indexes = indexes;
        ForeignKeys = foreignKeys;
    }
}
