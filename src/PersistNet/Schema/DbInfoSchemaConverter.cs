using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;

namespace PersistNet.Schema;

/// <summary>
/// Converts a <see cref="Database"/> (desired state derived from entity metadata)
/// into a <see cref="SchemaSnapshot"/> suitable for schema comparison and DDL generation.
/// </summary>
internal static class DbInfoSchemaConverter
{
    public static SchemaSnapshot Convert(Database database)
    {
        var tableByType = database.Tables.ToDictionary(t => t.EntityType);
        var tables = new List<SchemaTable>();

        foreach (var table in database.Tables)
        {
            tables.Add(BuildSchemaTable(table, tableByType));

            // M2M owning side generates a standalone join table
            foreach (var rel in table.Relationships.OfType<ManyToManyRelationship>())
            {
                if (rel.MappedBy == null)
                    tables.Add(BuildJoinTable(table, rel, tableByType));
            }
        }

        return new SchemaSnapshot(tables);
    }

    private static SchemaTable BuildSchemaTable(
        Table table,
        Dictionary<Type, Table> tableByType)
    {
        var columns = table.Columns
            .Select(c => MapColumn(c))
            .ToList();

        // Subtype extra columns are forced nullable (single-table inheritance)
        foreach (var subType in table.SubTypes)
        {
            foreach (var col in subType.ExtraColumns)
                columns.Add(MapColumn(col, nullableOverride: true));
        }

        var pkColumns = table.Columns
            .Where(c => c.IsKey)
            .OrderBy(c => c.KeyOrder)
            .Select(c => c.ColumnName)
            .ToArray();

        var primaryKey = pkColumns.Length > 0
            ? new SchemaPrimaryKey(null, pkColumns)
            : null;

        var indexes = table.Indexes
            .Select(i => new SchemaIndex(
                i.Name ?? $"idx_{table.Name}_{string.Join("_", i.Columns)}",
                i.Columns,
                i.Unique))
            .ToList();

        var foreignKeys = BuildForeignKeys(table, tableByType);

        return new SchemaTable(table.Name, table.Schema, primaryKey, columns, indexes, foreignKeys);
    }

    private static List<SchemaForeignKey> BuildForeignKeys(
        Table table,
        Dictionary<Type, Table> tableByType)
    {
        var fks = new List<SchemaForeignKey>();

        foreach (var rel in table.Relationships)
        {
            switch (rel)
            {
                case ManyToOneRelationship m2o
                    when m2o.RelatedType != null && tableByType.TryGetValue(m2o.RelatedType, out var m2oTarget):
                    fks.Add(new SchemaForeignKey(
                        m2o.Name,
                        m2o.FromKeys,
                        m2oTarget.Name,
                        m2oTarget.Schema,
                        m2o.ToKeys.Length > 0 ? m2o.ToKeys : GetPkColumns(m2oTarget),
                        m2o.OnDelete,
                        m2o.OnUpdate));
                    break;

                // Only the owning side (MappedBy == null) carries the FK
                case OneToOneRelationship o2o
                    when o2o.MappedBy == null && o2o.RelatedType != null && tableByType.TryGetValue(o2o.RelatedType, out var o2oTarget):
                    fks.Add(new SchemaForeignKey(
                        o2o.Name,
                        o2o.FromKeys,
                        o2oTarget.Name,
                        o2oTarget.Schema,
                        o2o.ToKeys.Length > 0 ? o2o.ToKeys : GetPkColumns(o2oTarget),
                        o2o.OnDelete,
                        o2o.OnUpdate));
                    break;
            }
        }

        return fks;
    }

    private static SchemaTable BuildJoinTable(
        Table ownerTable,
        ManyToManyRelationship rel,
        Dictionary<Type, Table> tableByType)
    {
        tableByType.TryGetValue(rel.RelatedType!, out var relatedTable);

        var leftSourceCols = rel.LeftForeignKeys.Length > 0
            ? rel.LeftForeignKeys
            : GetPkColumns(ownerTable);

        var rightSourceCols = rel.RightForeignKeys.Length > 0
            ? rel.RightForeignKeys
            : (relatedTable != null ? GetPkColumns(relatedTable) : Array.Empty<string>());

        var columns = new List<SchemaColumn>();
        columns.AddRange(BuildJoinColumns(rel.LeftKeyColumns, leftSourceCols, ownerTable));
        if (relatedTable != null)
            columns.AddRange(BuildJoinColumns(rel.RightKeyColumns, rightSourceCols, relatedTable));
        else
            columns.AddRange(rel.RightKeyColumns.Select(c => new SchemaColumn(c, "UNKNOWN", false, false, null, null, null, null)));

        var pkColumns = rel.LeftKeyColumns.Concat(rel.RightKeyColumns).ToArray();
        var primaryKey = pkColumns.Length > 0 ? new SchemaPrimaryKey(null, pkColumns) : null;

        var leftFk = new SchemaForeignKey(
            null, rel.LeftKeyColumns, ownerTable.Name, ownerTable.Schema, leftSourceCols,
            rel.OnDelete, rel.OnUpdate);

        var fks = new List<SchemaForeignKey> { leftFk };

        if (relatedTable != null)
        {
            fks.Add(new SchemaForeignKey(
                null, rel.RightKeyColumns, relatedTable.Name, relatedTable.Schema, rightSourceCols,
                rel.OnDelete, rel.OnUpdate));
        }

        var joinTableName = rel.JoinTableName
            ?? $"{ownerTable.Name}_{relatedTable?.Name ?? rel.RelatedType?.Name}";

        return new SchemaTable(joinTableName, rel.JoinTableSchema, primaryKey, columns, [], fks);
    }

    private static IEnumerable<SchemaColumn> BuildJoinColumns(
        string[] joinColumnNames,
        string[] sourceColumnNames,
        Table sourceTable)
    {
        var sourceByName = sourceTable.Columns
            .ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        return joinColumnNames.Zip(sourceColumnNames, (joinCol, sourceCol) =>
        {
            if (sourceByName.TryGetValue(sourceCol, out var source))
                return new SchemaColumn(joinCol, MapColumnType(source.Type), false, false, null, source.Size, source.Precision, source.Scale);

            return new SchemaColumn(joinCol, "UNKNOWN", false, false, null, null, null, null);
        });
    }

    private static string[] GetPkColumns(Table table) =>
        table.Columns
            .Where(c => c.IsKey)
            .OrderBy(c => c.KeyOrder)
            .Select(c => c.ColumnName)
            .ToArray();

    private static SchemaColumn MapColumn(DbInfo.Column column, bool? nullableOverride = null) =>
        new SchemaColumn(
            column.ColumnName,
            MapColumnType(column.Type),
            nullableOverride ?? column.Nullable,
            column.AutoIncrement,
            column.DefaultValue,
            column.Size,
            column.Precision,
            column.Scale);

    private static string MapColumnType(ColumnType? type) => type switch
    {
        ColumnType.Guid      => "GUID",
        ColumnType.Long      => "BIGINT",
        ColumnType.Boolean   => "BOOLEAN",
        ColumnType.Char      => "CHAR",
        ColumnType.Integer   => "INTEGER",
        ColumnType.Date      => "DATE",
        ColumnType.Double    => "DOUBLE",
        ColumnType.Float     => "FLOAT",
        ColumnType.Timestamp => "TIMESTAMP",
        ColumnType.Varchar   => "VARCHAR",
        ColumnType.Decimal   => "DECIMAL",
        ColumnType.Version   => "BIGINT",
        _                    => "UNKNOWN",
    };
}
