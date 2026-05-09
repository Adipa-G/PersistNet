using System;
using System.Collections.Generic;

namespace PersistNet.DbInfo;

internal sealed class Table
{
    public string Name { get; }
    public string? Schema { get; }
    public Type EntityType { get; }
    public IReadOnlyList<Column> Columns { get; }
    public Column? DiscriminatorColumn { get; }
    public IReadOnlyList<SubType> SubTypes { get; }
    public IReadOnlyList<Relationship> Relationships { get; }
    public IReadOnlyList<Index> Indexes { get; }

    /// <summary>
    /// For joined-subtype tables, the base table that holds the shared columns.
    /// When non-null, the entity's data is split across this table (subtype-own columns)
    /// and <see cref="BaseTable"/> (shared columns), joined by primary key.
    /// <c>null</c> for root tables and STI tables.
    /// </summary>
    public Table? BaseTable { get; }

    internal Table(
        string name,
        string? schema,
        Type entityType,
        IReadOnlyList<Column> columns,
        Column? discriminatorColumn,
        IReadOnlyList<SubType> subTypes,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyList<Index> indexes,
        Table? baseTable = null)
    {
        Name = name;
        Schema = schema;
        EntityType = entityType;
        Columns = columns;
        DiscriminatorColumn = discriminatorColumn;
        SubTypes = subTypes;
        Relationships = relationships;
        Indexes = indexes;
        BaseTable = baseTable;
    }
}
