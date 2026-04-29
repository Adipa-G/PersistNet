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

    internal Table(
        string name,
        string? schema,
        Type entityType,
        IReadOnlyList<Column> columns,
        Column? discriminatorColumn,
        IReadOnlyList<SubType> subTypes,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyList<Index> indexes)
    {
        Name = name;
        Schema = schema;
        EntityType = entityType;
        Columns = columns;
        DiscriminatorColumn = discriminatorColumn;
        SubTypes = subTypes;
        Relationships = relationships;
        Indexes = indexes;
    }
}
