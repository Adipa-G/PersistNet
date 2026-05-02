using PersistNet.Schema;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

/// <summary>
/// Abstract base that provides ANSI-SQL DDL generation for the operations whose
/// structure is identical (or near-identical) across SQL Server and SQLite.
/// Subclasses override type mapping, identifier quoting, and the handful of
/// operations whose syntax genuinely differs between engines.
/// </summary>
internal abstract class AnsiSqlSchemaBase : IDbSchema
{
    protected DbConnection Connection { get; }

    protected AnsiSqlSchemaBase(DbConnection connection)
    {
        Connection = connection;
    }

    // ── Abstract / virtual hooks ───────────────────────────────────────────

    /// <summary>Maps a canonical <see cref="SchemaColumn.DbType"/> to the vendor SQL type string.</summary>
    protected abstract string MapType(SchemaColumn column);

    /// <summary>Inline auto-increment clause appended to a column definition (e.g. "IDENTITY(1,1)").</summary>
    protected abstract string AutoIncrementClause { get; }

    /// <summary>Wraps an identifier in the vendor quoting style. Default: ANSI double-quotes.</summary>
    protected virtual string QuoteIdentifier(string name) => $"\"{name}\"";

    protected string QualifiedTable(string name, string? schema) =>
        schema is not null
            ? $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}"
            : QuoteIdentifier(name);

    protected virtual string FormatReferentialRule(ReferentialRuleType rule) => rule switch
    {
        ReferentialRuleType.Cascade   => "CASCADE",
        ReferentialRuleType.Restrict  => "RESTRICT",
        ReferentialRuleType.SetNull   => "SET NULL",
        ReferentialRuleType.DoNothing => "NO ACTION",
        _                             => "NO ACTION",
    };

    // ── SQL execution helpers ──────────────────────────────────────────────

    protected virtual async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    protected async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct))
            result.Add(map(reader));
        return result;
    }

    // ── CREATE TABLE ───────────────────────────────────────────────────────

    public virtual Task CreateTableAsync(SchemaTable table, CancellationToken ct = default)
        => ExecuteAsync(BuildCreateTableSql(table), ct);

    protected internal virtual string BuildCreateTableSql(SchemaTable table)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE {QualifiedTable(table.Name, table.Schema)} (");

        var parts = table.Columns.Select(BuildColumnDefinition).ToList();

        if (table.PrimaryKey?.Columns.Length > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKey.Columns.Select(QuoteIdentifier));
            var nameClause = table.PrimaryKey.Name is not null
                ? $"CONSTRAINT {QuoteIdentifier(table.PrimaryKey.Name)} "
                : "";
            parts.Add($"{nameClause}PRIMARY KEY ({pkCols})");
        }

        sb.Append(string.Join(", ", parts));
        sb.Append(')');
        return sb.ToString();
    }

    protected virtual string BuildColumnDefinition(SchemaColumn column)
    {
        var sb = new StringBuilder($"{QuoteIdentifier(column.Name)} {MapType(column)}");
        if (column.IsAutoIncrement) sb.Append($" {AutoIncrementClause}");
        if (!column.IsNullable)     sb.Append(" NOT NULL");
        if (column.DefaultValue is not null) sb.Append($" DEFAULT {column.DefaultValue}");
        return sb.ToString();
    }

    // ── DROP TABLE ─────────────────────────────────────────────────────────

    public Task DropTableAsync(string tableName, string? tableSchema, CancellationToken ct = default)
        => ExecuteAsync(BuildDropTableSql(tableName, tableSchema), ct);

    protected internal string BuildDropTableSql(string tableName, string? tableSchema)
        => $"DROP TABLE {QualifiedTable(tableName, tableSchema)}";

    // ── ADD COLUMN ─────────────────────────────────────────────────────────

    public virtual Task AddColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default)
        => ExecuteAsync(BuildAddColumnSql(tableName, tableSchema, column), ct);

    protected internal virtual string BuildAddColumnSql(string tableName, string? tableSchema, SchemaColumn column)
        => $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} ADD COLUMN {BuildColumnDefinition(column)}";

    // ── DROP COLUMN ────────────────────────────────────────────────────────

    public Task DropColumnAsync(string tableName, string? tableSchema, string columnName, CancellationToken ct = default)
        => ExecuteAsync(BuildDropColumnSql(tableName, tableSchema, columnName), ct);

    protected internal string BuildDropColumnSql(string tableName, string? tableSchema, string columnName)
        => $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} DROP COLUMN {QuoteIdentifier(columnName)}";

    // ── CREATE INDEX ───────────────────────────────────────────────────────

    public Task CreateIndexAsync(string tableName, string? tableSchema, SchemaIndex index, CancellationToken ct = default)
        => ExecuteAsync(BuildCreateIndexSql(tableName, tableSchema, index), ct);

    protected internal string BuildCreateIndexSql(string tableName, string? tableSchema, SchemaIndex index)
    {
        var unique    = index.IsUnique ? "UNIQUE " : "";
        var indexName = index.Name is not null ? $"{QuoteIdentifier(index.Name)} " : "";
        var cols      = string.Join(", ", index.Columns.Select(QuoteIdentifier));
        return $"CREATE {unique}INDEX {indexName}ON {QualifiedTable(tableName, tableSchema)} ({cols})";
    }

    // ── ADD FOREIGN KEY ────────────────────────────────────────────────────

    public virtual Task AddForeignKeyAsync(string tableName, string? tableSchema, SchemaForeignKey foreignKey, CancellationToken ct = default)
        => ExecuteAsync(BuildAddForeignKeySql(tableName, tableSchema, foreignKey), ct);

    protected internal virtual string BuildAddForeignKeySql(string tableName, string? tableSchema, SchemaForeignKey foreignKey)
    {
        var from       = string.Join(", ", foreignKey.FromColumns.Select(QuoteIdentifier));
        var to         = string.Join(", ", foreignKey.ToColumns.Select(QuoteIdentifier));
        var constraint = foreignKey.Name is not null
            ? $"CONSTRAINT {QuoteIdentifier(foreignKey.Name)} "
            : "";

        var sb = new StringBuilder(
            $"ALTER TABLE {QualifiedTable(tableName, tableSchema)} " +
            $"ADD {constraint}FOREIGN KEY ({from}) " +
            $"REFERENCES {QualifiedTable(foreignKey.ToTable, foreignKey.ToSchema)} ({to})");

        if (foreignKey.OnDelete.HasValue)
            sb.Append($" ON DELETE {FormatReferentialRule(foreignKey.OnDelete.Value)}");
        if (foreignKey.OnUpdate.HasValue)
            sb.Append($" ON UPDATE {FormatReferentialRule(foreignKey.OnUpdate.Value)}");

        return sb.ToString();
    }

    // ── GenerateDiffSql ────────────────────────────────────────────────────

    /// <summary>
    /// Converts a <see cref="SchemaDiff"/> into the ordered SQL statements required to
    /// migrate from <paramref name="actual"/> to <paramref name="desired"/>.
    /// <para>
    /// The base implementation handles all operations that have provider-agnostic
    /// <c>Build*Sql</c> methods here. Subclasses should call <c>base.GenerateDiffSql</c>
    /// and then append their provider-specific statements (ALTER COLUMN, DROP INDEX, etc.).
    /// </para>
    /// </summary>
    internal virtual IReadOnlyList<string> GenerateDiffSql(SchemaDiff diff, SchemaSnapshot desired, SchemaSnapshot actual)
    {
        var sql = new List<string>();

        foreach (var table in diff.TablesToCreate)
            sql.Add(BuildCreateTableSql(table));

        foreach (var (tableName, tableSchema, col) in diff.ColumnsToAdd)
            sql.Add(BuildAddColumnSql(tableName, tableSchema, col));

        foreach (var (tableName, tableSchema, index) in diff.IndexesToCreate)
            sql.Add(BuildCreateIndexSql(tableName, tableSchema, index));

        foreach (var (tableName, tableSchema, fk) in diff.ForeignKeysToAdd)
            sql.Add(BuildAddForeignKeySql(tableName, tableSchema, fk));

        foreach (var (tableName, tableSchema, colName) in diff.ColumnsToDrop)
            sql.Add(BuildDropColumnSql(tableName, tableSchema, colName));

        foreach (var (tableName, tableSchema) in diff.TablesToDrop)
            sql.Add(BuildDropTableSql(tableName, tableSchema));

        return sql;
    }

    // ── Abstract (provider-specific) ──────────────────────────────────────

    public abstract Task<SchemaSnapshot> GetCurrentSchemaAsync(CancellationToken ct = default);
    public abstract Task AlterColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default);
    public abstract Task DropIndexAsync(string tableName, string? tableSchema, string indexName, CancellationToken ct = default);
    public abstract Task DropForeignKeyAsync(string tableName, string? tableSchema, string foreignKeyName, CancellationToken ct = default);
}
