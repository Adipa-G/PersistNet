using System.Collections.Generic;

namespace PersistNet.DbAbstraction;

internal sealed class SchemaDiff
{
    public IReadOnlyList<SchemaTable> TablesToCreate { get; }
    public IReadOnlyList<(string Name, string? Schema)> TablesToDrop { get; }

    public IReadOnlyList<(string TableName, string? TableSchema, SchemaColumn Column)> ColumnsToAdd { get; }
    public IReadOnlyList<(string TableName, string? TableSchema, SchemaColumn Column)> ColumnsToAlter { get; }
    public IReadOnlyList<(string TableName, string? TableSchema, string ColumnName)> ColumnsToDrop { get; }

    public IReadOnlyList<(string TableName, string? TableSchema, SchemaIndex Index)> IndexesToCreate { get; }
    public IReadOnlyList<(string TableName, string? TableSchema, string IndexName)> IndexesToDrop { get; }

    public IReadOnlyList<(string TableName, string? TableSchema, SchemaForeignKey ForeignKey)> ForeignKeysToAdd { get; }
    public IReadOnlyList<(string TableName, string? TableSchema, string ForeignKeyName)> ForeignKeysToDrop { get; }

    public bool IsEmpty =>
        TablesToCreate.Count == 0 &&
        TablesToDrop.Count == 0 &&
        ColumnsToAdd.Count == 0 &&
        ColumnsToAlter.Count == 0 &&
        ColumnsToDrop.Count == 0 &&
        IndexesToCreate.Count == 0 &&
        IndexesToDrop.Count == 0 &&
        ForeignKeysToAdd.Count == 0 &&
        ForeignKeysToDrop.Count == 0;

    internal SchemaDiff(
        IReadOnlyList<SchemaTable> tablesToCreate,
        IReadOnlyList<(string, string?)> tablesToDrop,
        IReadOnlyList<(string, string?, SchemaColumn)> columnsToAdd,
        IReadOnlyList<(string, string?, SchemaColumn)> columnsToAlter,
        IReadOnlyList<(string, string?, string)> columnsToDrop,
        IReadOnlyList<(string, string?, SchemaIndex)> indexesToCreate,
        IReadOnlyList<(string, string?, string)> indexesToDrop,
        IReadOnlyList<(string, string?, SchemaForeignKey)> foreignKeysToAdd,
        IReadOnlyList<(string, string?, string)> foreignKeysToDrop)
    {
        TablesToCreate = tablesToCreate;
        TablesToDrop = tablesToDrop;
        ColumnsToAdd = columnsToAdd;
        ColumnsToAlter = columnsToAlter;
        ColumnsToDrop = columnsToDrop;
        IndexesToCreate = indexesToCreate;
        IndexesToDrop = indexesToDrop;
        ForeignKeysToAdd = foreignKeysToAdd;
        ForeignKeysToDrop = foreignKeysToDrop;
    }
}
