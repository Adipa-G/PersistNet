using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

internal sealed class SqlServerSchema : AnsiSqlSchemaBase
{
    internal SqlServerSchema(DbConnection connection) : base(connection) { }

    // ── Identity / quoting ─────────────────────────────────────────────────

    protected override string QuoteIdentifier(string name) => $"[{name}]";

    protected override string AutoIncrementClause => "IDENTITY(1,1)";

    // ── Type mapping ───────────────────────────────────────────────────────

    protected override string MapType(SchemaColumn column) => column.DbType switch
    {
        "GUID"      => "UNIQUEIDENTIFIER",
        "BIGINT"    => "BIGINT",
        "BOOLEAN"   => "BIT",
        "CHAR"      => "CHAR(1)",
        "INTEGER"   => "INT",
        "DATE"      => "DATE",
        "DOUBLE"    => "FLOAT",
        "FLOAT"     => "REAL",
        "TIMESTAMP" => "DATETIME2",
        "VARCHAR"   => column.Size.HasValue ? $"NVARCHAR({column.Size})" : "NVARCHAR(MAX)",
        "DECIMAL"   => column.Precision.HasValue && column.Scale.HasValue
                           ? $"DECIMAL({column.Precision},{column.Scale})"
                           : "DECIMAL(18,2)",
        _           => column.DbType,
    };

    // ── ADD COLUMN — SQL Server uses ADD, not ADD COLUMN ───────────────────

    protected internal override string BuildAddColumnSql(string tableName, string? tableSchema, SchemaColumn column)
        => $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} ADD {BuildColumnDefinition(column)}";

    // ── ALTER COLUMN ───────────────────────────────────────────────────────

    internal string BuildAlterColumnSql(string tableName, string? tableSchema, SchemaColumn column)
    {
        var nullability = column.IsNullable ? "NULL" : "NOT NULL";
        return $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} " +
               $"ALTER COLUMN {QuoteIdentifier(column.Name)} {MapType(column)} {nullability}";
    }

    public override Task AlterColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default)
        => ExecuteAsync(BuildAlterColumnSql(tableName, tableSchema, column), ct);

    // ── DROP INDEX — SQL Server requires ON <table> ────────────────────────

    internal string BuildDropIndexSql(string tableName, string? tableSchema, string indexName)
        => $"DROP INDEX {QuoteIdentifier(indexName)} ON {QualifiedTable(tableName, tableSchema)}";

    public override Task DropIndexAsync(string tableName, string? tableSchema, string indexName, CancellationToken ct = default)
        => ExecuteAsync(BuildDropIndexSql(tableName, tableSchema, indexName), ct);

    // ── DROP FOREIGN KEY ───────────────────────────────────────────────────

    internal string BuildDropForeignKeySql(string tableName, string? tableSchema, string foreignKeyName)
        => $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} DROP CONSTRAINT {QuoteIdentifier(foreignKeyName)}";

    public override Task DropForeignKeyAsync(string tableName, string? tableSchema, string foreignKeyName, CancellationToken ct = default)
        => ExecuteAsync(BuildDropForeignKeySql(tableName, tableSchema, foreignKeyName), ct);

    // ── GetCurrentSchemaAsync — reads sys.* catalog views ─────────────────

    public override async Task<SchemaSnapshot> GetCurrentSchemaAsync(CancellationToken ct = default)
    {
        var tableRows = await QueryAsync(
            "SELECT t.object_id, t.name, s.name " +
            "FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id " +
            "ORDER BY s.name, t.name",
            r => (ObjectId: Convert.ToInt32(r[0]), Name: (string)r[1], Schema: (string)r[2]),
            ct);

        var tables = new List<SchemaTable>();

        foreach (var (objectId, tableName, tableSchema) in tableRows)
        {
            var columns     = await ReadColumnsAsync(objectId, ct);
            var pk          = await ReadPrimaryKeyAsync(objectId, ct);
            var indexes     = await ReadIndexesAsync(objectId, ct);
            var foreignKeys = await ReadForeignKeysAsync(objectId, ct);

            tables.Add(new SchemaTable(tableName, tableSchema, pk, columns, indexes, foreignKeys));
        }

        return new SchemaSnapshot(tables);
    }

    private async Task<List<SchemaColumn>> ReadColumnsAsync(int objectId, CancellationToken ct)
    {
        var rows = await QueryAsync(@"
            SELECT c.name, tp.name, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, dc.definition
            FROM sys.columns c
            JOIN sys.types tp ON c.user_type_id = tp.user_type_id
            LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
            WHERE c.object_id = @id
            ORDER BY c.column_id",
            r => (
                Name:      (string)r[0],
                TypeName:  (string)r[1],
                MaxLen:    Convert.ToInt32(r[2]),
                Precision: Convert.ToInt32(r[3]),
                Scale:     Convert.ToInt32(r[4]),
                Nullable:  (bool)r[5],
                Identity:  (bool)r[6],
                Default:   r.IsDBNull(7) ? null : (string?)r[7]),
            ct, ("@id", objectId));

        return rows.Select(r => new SchemaColumn(
            r.Name,
            NormalizeType(r.TypeName),
            r.Nullable,
            r.Identity,
            r.Default,
            GetSize(r.TypeName, r.MaxLen),
            r.Precision > 0 ? r.Precision : null,
            r.Scale > 0 ? r.Scale : null)).ToList();
    }

    private async Task<SchemaPrimaryKey?> ReadPrimaryKeyAsync(int objectId, CancellationToken ct)
    {
        var cols = await QueryAsync(@"
            SELECT col.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id
            WHERE i.object_id = @id AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal",
            r => (string)r[0],
            ct, ("@id", objectId));

        return cols.Count > 0 ? new SchemaPrimaryKey(null, cols.ToArray()) : null;
    }

    private async Task<List<SchemaIndex>> ReadIndexesAsync(int objectId, CancellationToken ct)
    {
        var indexNames = await QueryAsync(@"
            SELECT DISTINCT i.name, i.is_unique
            FROM sys.indexes i
            WHERE i.object_id = @id
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.name IS NOT NULL
            ORDER BY i.name",
            r => (Name: (string)r[0], IsUnique: (bool)r[1]),
            ct, ("@id", objectId));

        var result = new List<SchemaIndex>();
        foreach (var (name, isUnique) in indexNames)
        {
            var cols = await QueryAsync(@"
                SELECT col.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id
                WHERE i.object_id = @id AND i.name = @name
                ORDER BY ic.key_ordinal",
                r => (string)r[0],
                ct, ("@id", objectId), ("@name", name));

            result.Add(new SchemaIndex(name, cols.ToArray(), isUnique));
        }
        return result;
    }

    private async Task<List<SchemaForeignKey>> ReadForeignKeysAsync(int objectId, CancellationToken ct)
    {
        var rows = await QueryAsync(@"
            SELECT fk.name,
                   fc.name,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id),
                   OBJECT_NAME(fk.referenced_object_id),
                   tc.name,
                   fk.delete_referential_action,
                   fk.update_referential_action
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns fc ON fkc.parent_object_id = fc.object_id AND fkc.parent_column_id = fc.column_id
            JOIN sys.columns tc ON fkc.referenced_object_id = tc.object_id AND fkc.referenced_column_id = tc.column_id
            WHERE fk.parent_object_id = @id
            ORDER BY fk.name, fkc.constraint_column_id",
            r => (
                FkName:    (string)r[0],
                FromCol:   (string)r[1],
                ToSchema:  r.IsDBNull(2) ? null : (string?)r[2],
                ToTable:   (string)r[3],
                ToCol:     (string)r[4],
                OnDelete:  Convert.ToByte(r[5]),
                OnUpdate:  Convert.ToByte(r[6])),
            ct, ("@id", objectId));

        return rows
            .GroupBy(r => r.FkName)
            .Select(g =>
            {
                var first = g.First();
                return new SchemaForeignKey(
                    first.FkName,
                    g.Select(r => r.FromCol).ToArray(),
                    first.ToTable,
                    first.ToSchema,
                    g.Select(r => r.ToCol).ToArray(),
                    ParseRefAction(first.OnDelete),
                    ParseRefAction(first.OnUpdate));
            })
            .ToList();
    }

    // ── Type helpers ───────────────────────────────────────────────────────

    private static string NormalizeType(string sqlServerType) =>
        sqlServerType.ToLowerInvariant() switch
        {
            "uniqueidentifier"                                              => "GUID",
            "bigint"                                                        => "BIGINT",
            "int" or "smallint" or "tinyint"                               => "INTEGER",
            "bit"                                                           => "BOOLEAN",
            "char" or "nchar"                                              => "CHAR",
            "varchar" or "nvarchar" or "text" or "ntext"                   => "VARCHAR",
            "float"                                                         => "DOUBLE",
            "real"                                                          => "FLOAT",
            "date"                                                          => "DATE",
            "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" => "TIMESTAMP",
            "decimal" or "numeric" or "money" or "smallmoney"              => "DECIMAL",
            "timestamp" or "rowversion"                                    => "BIGINT",
            _                                                               => sqlServerType.ToUpperInvariant(),
        };

    private static int? GetSize(string typeName, int maxLen)
    {
        if (maxLen <= 0) return null; // -1 = MAX, 0 = no size
        return typeName.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" or "ntext" => maxLen / 2,
            "varchar"  or "char"  or "text"  => maxLen,
            _ => null,
        };
    }

    private static ReferentialRuleType? ParseRefAction(byte action) => action switch
    {
        1 => ReferentialRuleType.Cascade,
        2 => ReferentialRuleType.SetNull,
        _ => null,
    };
}
