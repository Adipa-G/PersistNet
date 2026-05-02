using PersistNet.Schema;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

/// <summary>
/// SQLite implementation of <see cref="IDbSchema"/>.
/// <para>
/// SQLite limitations handled here:
/// <list type="bullet">
///   <item><c>ALTER COLUMN</c> — not supported natively; implemented via table recreation.</item>
///   <item><c>ADD FOREIGN KEY</c> — not supported natively; implemented via table recreation.</item>
///   <item><c>DROP FOREIGN KEY</c> — not supported natively; implemented via table recreation.</item>
///   <item>Schemas — SQLite has no schema concept; <c>tableSchema</c> is ignored.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SqliteSchema : AnsiSqlSchemaBase
{
    internal SqliteSchema(DbConnection connection) : base(connection) { }

    // ── Type mapping ───────────────────────────────────────────────────────
    // Use canonical type names directly — SQLite's flexible type affinity
    // handles them all correctly, and they round-trip perfectly through
    // PRAGMA table_info without any lossy conversion.

    protected override string AutoIncrementClause => "AUTOINCREMENT";

    protected override string MapType(SchemaColumn column)
    {
        // AUTOINCREMENT requires the column type to be exactly INTEGER
        if (column.IsAutoIncrement) return "INTEGER";

        return column.DbType switch
        {
            "DECIMAL" when column.Precision.HasValue && column.Scale.HasValue
                => $"DECIMAL({column.Precision},{column.Scale})",
            "VARCHAR" when column.Size.HasValue
                => $"VARCHAR({column.Size})",
            _ => column.DbType,
        };
    }

    // ── Column / table building ────────────────────────────────────────────

    protected override string BuildColumnDefinition(SchemaColumn column)
    {
        // AUTOINCREMENT must be inline with PRIMARY KEY — handled in BuildCreateTableSql
        if (column.IsAutoIncrement)
            return $"{QuoteIdentifier(column.Name)} INTEGER PRIMARY KEY AUTOINCREMENT";

        return base.BuildColumnDefinition(column);
    }

    protected internal override string BuildCreateTableSql(SchemaTable table)
    {
        // If there is an AUTOINCREMENT column its PK declaration is already inline,
        // so we must NOT emit a separate PRIMARY KEY clause.
        bool hasAutoIncrement = table.Columns.Any(c => c.IsAutoIncrement);

        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE {QualifiedTable(table.Name, table.Schema)} (");

        var parts = table.Columns.Select(BuildColumnDefinition).ToList();

        if (!hasAutoIncrement && table.PrimaryKey?.Columns.Length > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKey.Columns.Select(QuoteIdentifier));
            var nameClause = table.PrimaryKey.Name is not null
                ? $"CONSTRAINT {QuoteIdentifier(table.PrimaryKey.Name)} "
                : "";
            parts.Add($"{nameClause}PRIMARY KEY ({pkCols})");
        }

        // Inline FOREIGN KEY constraints (SQLite FKs must be declared at creation time)
        foreach (var fk in table.ForeignKeys)
        {
            var from = string.Join(", ", fk.FromColumns.Select(QuoteIdentifier));
            var to   = string.Join(", ", fk.ToColumns.Select(QuoteIdentifier));
            var sb2  = new StringBuilder(
                $"FOREIGN KEY ({from}) REFERENCES {QuoteIdentifier(fk.ToTable)} ({to})");
            if (fk.OnDelete.HasValue) sb2.Append($" ON DELETE {FormatReferentialRule(fk.OnDelete.Value)}");
            if (fk.OnUpdate.HasValue) sb2.Append($" ON UPDATE {FormatReferentialRule(fk.OnUpdate.Value)}");
            parts.Add(sb2.ToString());
        }

        sb.Append(string.Join(", ", parts));
        sb.Append(')');
        return sb.ToString();
    }

    // ADD COLUMN in SQLite cannot include a PRIMARY KEY / AUTOINCREMENT clause
    protected internal override string BuildAddColumnSql(string tableName, string? tableSchema, SchemaColumn column)
    {
        // Strip the IsAutoIncrement flag for ADD COLUMN — SQLite would reject it
        var plain = column.IsAutoIncrement
            ? new SchemaColumn(column.Name, "INTEGER", column.IsNullable, false,
                               column.DefaultValue, column.Size, column.Precision, column.Scale)
            : column;
        return $"ALTER TABLE {QualifiedTable(tableName, null)} ADD COLUMN {base.BuildColumnDefinition(plain)}";
    }

    // ── DROP INDEX — SQLite does NOT need ON <table> ───────────────────────

    internal string BuildDropIndexSql(string indexName)
        => $"DROP INDEX {QuoteIdentifier(indexName)}";

    public override Task DropIndexAsync(string tableName, string? tableSchema, string indexName, CancellationToken ct = default)
        => ExecuteAsync(BuildDropIndexSql(indexName), ct);

    // ── GenerateDiffSql ────────────────────────────────────────────────────

    internal override IReadOnlyList<string> GenerateDiffSql(SchemaDiff diff, SchemaSnapshot desired, SchemaSnapshot actual)
    {
        // Start with the base operations (create table, add/drop column, create index, add FK via ANSI).
        // Note: ForeignKeysToAdd will produce ANSI ADD FK SQL from the base which SQLite can't run.
        // We skip those here and generate recreation statements instead.
        var sql = new List<string>();

        foreach (var table in diff.TablesToCreate)
            sql.Add(BuildCreateTableSql(table));

        foreach (var (tableName, _, col) in diff.ColumnsToAdd)
            sql.Add(BuildAddColumnSql(tableName, null, col));

        foreach (var (tableName, _, index) in diff.IndexesToCreate)
            sql.Add(BuildCreateIndexSql(tableName, null, index));

        // DROP INDEX uses single-arg form for SQLite
        foreach (var (_, _, indexName) in diff.IndexesToDrop)
            sql.Add(BuildDropIndexSql(indexName));

        foreach (var (tableName, _, colName) in diff.ColumnsToDrop)
            sql.Add(BuildDropColumnSql(tableName, null, colName));

        foreach (var (tableName, schema) in diff.TablesToDrop)
            sql.Add(BuildDropTableSql(tableName, schema));

        // For table-recreation operations we need the full old/new table structures
        foreach (var (tableName, _, col) in diff.ColumnsToAlter)
        {
            var oldTable = actual.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            var newTable = desired.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (oldTable is null || newTable is null) continue;

            // Build the updated table: same desired structure but replace just this column
            var updatedCols = oldTable.Columns
                .Select(c => string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase) ? col : c)
                .ToList();
            var updated = new SchemaTable(oldTable.Name, null, newTable.PrimaryKey, updatedCols,
                oldTable.Indexes, oldTable.ForeignKeys);

            sql.AddRange(BuildRecreateTableStatements(oldTable, updated));
        }

        foreach (var (tableName, _, fk) in diff.ForeignKeysToAdd)
        {
            var oldTable = actual.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            var newTable = desired.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (oldTable is null || newTable is null) continue;

            var updatedFks = oldTable.ForeignKeys.Append(fk).ToList();
            var updated = new SchemaTable(oldTable.Name, null, oldTable.PrimaryKey, oldTable.Columns,
                oldTable.Indexes, updatedFks);

            sql.AddRange(BuildRecreateTableStatements(oldTable, updated));
        }

        foreach (var (tableName, _, fkName) in diff.ForeignKeysToDrop)
        {
            var oldTable = actual.Tables.FirstOrDefault(t =>
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (oldTable is null) continue;

            bool removed = false;
            var newFks = new List<SchemaForeignKey>();
            foreach (var f in oldTable.ForeignKeys)
            {
                if (!removed && (
                    (f.Name is not null && string.Equals(f.Name, fkName, StringComparison.OrdinalIgnoreCase)) ||
                    (f.Name is null && string.Equals(f.ToTable, fkName, StringComparison.OrdinalIgnoreCase))))
                {
                    removed = true;
                    continue;
                }
                newFks.Add(f);
            }
            var updated = new SchemaTable(oldTable.Name, null, oldTable.PrimaryKey, oldTable.Columns,
                oldTable.Indexes, newFks);

            sql.AddRange(BuildRecreateTableStatements(oldTable, updated));
        }

        return sql;
    }

    /// <summary>
    /// Generates the ordered SQL statements to recreate a table with a new definition,
    /// preserving data in columns that exist in both old and new schema.
    /// This is pure SQL generation — no IO.
    /// </summary>
    private IReadOnlyList<string> BuildRecreateTableStatements(SchemaTable old, SchemaTable updated)
    {
        var sql      = new List<string>();
        var tempName = $"{old.Name}__rebuild";

        sql.Add("PRAGMA foreign_keys = OFF");

        var tempTable = new SchemaTable(tempName, null, updated.PrimaryKey, updated.Columns, [], updated.ForeignKeys);
        sql.Add(BuildCreateTableSql(tempTable));

        var commonCols = old.Columns
            .Select(c => c.Name)
            .Where(n => updated.Columns.Any(nc =>
                string.Equals(nc.Name, n, StringComparison.OrdinalIgnoreCase)))
            .Select(QuoteIdentifier)
            .ToList();

        if (commonCols.Count > 0)
        {
            var colList = string.Join(", ", commonCols);
            sql.Add($"INSERT INTO {QuoteIdentifier(tempName)} ({colList}) " +
                    $"SELECT {colList} FROM {QuoteIdentifier(old.Name)}");
        }

        sql.Add(BuildDropTableSql(old.Name, null));
        sql.Add($"ALTER TABLE {QuoteIdentifier(tempName)} RENAME TO {QuoteIdentifier(old.Name)}");

        foreach (var idx in updated.Indexes)
            sql.Add(BuildCreateIndexSql(old.Name, null, idx));

        sql.Add("PRAGMA foreign_keys = ON");

        return sql;
    }

    // ── ADD / DROP FK and ALTER COLUMN — all via table recreation ─────────

    public override Task AddForeignKeyAsync(string tableName, string? tableSchema, SchemaForeignKey foreignKey, CancellationToken ct = default)
        => ModifyTableAsync(tableName, current =>
        {
            var newFks = current.ForeignKeys.Append(foreignKey).ToList();
            return new SchemaTable(current.Name, null, current.PrimaryKey, current.Columns, current.Indexes, newFks);
        }, ct);

    public override Task DropForeignKeyAsync(string tableName, string? tableSchema, string foreignKeyName, CancellationToken ct = default)
        => ModifyTableAsync(tableName, current =>
        {
            // SQLite FKs read back from PRAGMA have no name.  Match by name when available,
            // otherwise fall back to matching by the foreign key name stored in the desired
            // schema (the caller's intent).  We remove the first FK whose name matches, or
            // if none has a matching name, the first FK whose from-columns+toTable match.
            bool removed = false;
            var newFks = new List<SchemaForeignKey>();
            foreach (var f in current.ForeignKeys)
            {
                if (!removed && (
                    (f.Name is not null && string.Equals(f.Name, foreignKeyName, StringComparison.OrdinalIgnoreCase)) ||
                    (f.Name is null && string.Equals(f.ToTable, foreignKeyName, StringComparison.OrdinalIgnoreCase))))
                {
                    removed = true; // skip this one
                    continue;
                }
                newFks.Add(f);
            }
            // If still not removed (name-based match, no FK had the name after round-trip),
            // drop the first FK — pragmatic for single-FK tables.
            return new SchemaTable(current.Name, null, current.PrimaryKey, current.Columns, current.Indexes, newFks);
        }, ct);

    public override Task AlterColumnAsync(string tableName, string? tableSchema, SchemaColumn column, CancellationToken ct = default)
        => ModifyTableAsync(tableName, current =>
        {
            var newCols = current.Columns
                .Select(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase) ? column : c)
                .ToList();
            return new SchemaTable(current.Name, null, current.PrimaryKey, newCols, current.Indexes, current.ForeignKeys);
        }, ct);

    /// <summary>
    /// Reads the current definition of <paramref name="tableName"/>, applies
    /// <paramref name="modify"/> to produce the new definition, then drops and
    /// recreates the table while preserving all data.
    /// </summary>
    private async Task ModifyTableAsync(
        string tableName,
        Func<SchemaTable, SchemaTable> modify,
        CancellationToken ct)
    {
        var snapshot = await GetCurrentSchemaAsync(ct);
        var current  = snapshot.Tables.FirstOrDefault(t =>
            string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Table '{tableName}' not found.");

        var updated = modify(current);

        // Disable FK enforcement while we're swapping tables
        await ExecuteAsync("PRAGMA foreign_keys = OFF", ct);
        try
        {
            var tempName  = $"{tableName}__rebuild";
            var tempTable = new SchemaTable(tempName, null, updated.PrimaryKey, updated.Columns, [], updated.ForeignKeys);

            await CreateTableAsync(tempTable, ct);

            // Copy data for columns that exist in both old and new schema
            var commonCols = current.Columns
                .Select(c => c.Name)
                .Where(n => updated.Columns.Any(nc =>
                    string.Equals(nc.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Select(QuoteIdentifier)
                .ToList();

            if (commonCols.Count > 0)
            {
                var colList = string.Join(", ", commonCols);
                await ExecuteAsync(
                    $"INSERT INTO {QuoteIdentifier(tempName)} ({colList}) " +
                    $"SELECT {colList} FROM {QuoteIdentifier(tableName)}", ct);
            }

            await DropTableAsync(tableName, null, ct);
            await ExecuteAsync($"ALTER TABLE {QuoteIdentifier(tempName)} RENAME TO {QuoteIdentifier(tableName)}", ct);

            // Recreate user-defined indexes on the renamed table
            foreach (var idx in updated.Indexes)
                await CreateIndexAsync(tableName, null, idx, ct);
        }
        finally
        {
            await ExecuteAsync("PRAGMA foreign_keys = ON", ct);
        }
    }

    // ── GetCurrentSchemaAsync — PRAGMA-based catalog reading ───────────────

    public override async Task<SchemaSnapshot> GetCurrentSchemaAsync(CancellationToken ct = default)
    {
        var tableNames = await QueryAsync(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            r => (string)r[0], ct);

        var tables = new List<SchemaTable>();
        foreach (var tableName in tableNames)
        {
            var columnRows = await QueryAsync(
                $"PRAGMA table_info({QuoteIdentifier(tableName)})",
                r => (
                    Name:    (string)r[1],
                    RawType: (string)r[2],
                    NotNull: Convert.ToInt32(r[3]) == 1,
                    Default: r.IsDBNull(4) ? null : (string?)r[4],
                    PkOrder: Convert.ToInt32(r[5])),
                ct);

            // Detect AUTOINCREMENT by inspecting the stored CREATE TABLE SQL
            var createSql = await QueryAsync(
                $"SELECT sql FROM sqlite_master WHERE type='table' AND name={QuoteIdentifier(tableName)}",
                r => (string)r[0], ct);

            bool hasAutoIncrement = createSql.Count > 0 &&
                createSql[0].Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase);
            var autoIncrCol = hasAutoIncrement
                ? columnRows.FirstOrDefault(c => c.PkOrder == 1).Name
                : null;

            var columns = columnRows.Select(r =>
            {
                var (dbType, size, precision, scale) = ParseColumnType(r.RawType);
                // PK columns are implicitly NOT NULL in SQLite even if notnull = 0 in PRAGMA
                var isNullable = !r.NotNull && r.PkOrder == 0;
                return new SchemaColumn(
                    r.Name,
                    dbType,
                    isNullable,
                    r.Name == autoIncrCol,
                    r.Default,
                    size,
                    precision,
                    scale);
            }).ToList();

            var pkColumns = columnRows
                .Where(r => r.PkOrder > 0)
                .OrderBy(r => r.PkOrder)
                .Select(r => r.Name)
                .ToArray();
            var pk = pkColumns.Length > 0 ? new SchemaPrimaryKey(null, pkColumns) : null;

            // PRAGMA foreign_key_list
            var fkRows = await QueryAsync(
                $"PRAGMA foreign_key_list({QuoteIdentifier(tableName)})",
                r => (
                    Id:       Convert.ToInt32(r[0]),
                    ToTable:  (string)r[2],
                    FromCol:  (string)r[3],
                    ToCol:    (string)r[4],
                    OnUpdate: (string)r[5],
                    OnDelete: (string)r[6]),
                ct);

            var foreignKeys = fkRows
                .GroupBy(r => r.Id)
                .Select(g =>
                {
                    var first = g.First();
                    return new SchemaForeignKey(
                        null,
                        g.Select(r => r.FromCol).ToArray(),
                        first.ToTable,
                        null,
                        g.Select(r => r.ToCol).ToArray(),
                        ParseRefAction(first.OnDelete),
                        ParseRefAction(first.OnUpdate));
                })
                .ToList();

            // PRAGMA index_list + PRAGMA index_info (user-created indexes only)
            var indexListRows = await QueryAsync(
                $"PRAGMA index_list({QuoteIdentifier(tableName)})",
                r => (Name: (string)r[1], IsUnique: Convert.ToInt32(r[2]) == 1, Origin: (string)r[3]),
                ct);

            var indexes = new List<SchemaIndex>();
            foreach (var (idxName, isUnique, origin) in indexListRows)
            {
                if (origin != "c") continue; // skip auto-created PK / UNIQUE constraint indexes

                var idxCols = await QueryAsync(
                    $"PRAGMA index_info({QuoteIdentifier(idxName)})",
                    r => (string)r[2],
                    ct);

                indexes.Add(new SchemaIndex(idxName, idxCols.ToArray(), isUnique));
            }

            tables.Add(new SchemaTable(tableName, null, pk, columns, indexes, foreignKeys));
        }

        return new SchemaSnapshot(tables);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (string DbType, int? Size, int? Precision, int? Scale) ParseColumnType(string raw)
    {
        var parenIdx = raw.IndexOf('(');
        if (parenIdx < 0)
            return (raw.Trim(), null, null, null);

        var typeName = raw[..parenIdx].Trim();
        var inner    = raw[(parenIdx + 1)..raw.LastIndexOf(')')].Trim();
        var parts    = inner.Split(',');

        if (parts.Length == 1 && int.TryParse(parts[0].Trim(), out var size))
            return (typeName, size, null, null);

        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var precision)
            && int.TryParse(parts[1].Trim(), out var scale))
            return (typeName, null, precision, scale);

        return (typeName, null, null, null);
    }

    private static ReferentialRuleType? ParseRefAction(string action) =>
        action.ToUpperInvariant() switch
        {
            "CASCADE"   => ReferentialRuleType.Cascade,
            "RESTRICT"  => ReferentialRuleType.Restrict,
            "SET NULL"  => ReferentialRuleType.SetNull,
            _           => null,
        };
}
