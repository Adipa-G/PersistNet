using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;
using PersistNet.Schema;

namespace PersistNet;

/// <summary>
/// Public entry-point for schema management.
/// Compares the schema derived from annotated entity types against the live database
/// and can either apply the required DDL changes or export them as SQL strings.
/// </summary>
/// <example>
/// <code>
/// using var connection = new SqliteConnection("Data Source=app.db");
/// connection.Open();
///
/// var upgrader = SchemaUpgrader.FromAssembly(connection, DbProvider.SQLite,
///     typeof(Order).Assembly);
///
/// if (!await upgrader.IsUpToDateAsync())
///     await upgrader.ApplyAsync();
/// </code>
/// </example>
public sealed class SchemaUpgrader
{
    private readonly AnsiSqlSchemaBase _schema;
    private readonly IEnumerable<Type> _entityTypes;

    private SchemaUpgrader(AnsiSqlSchemaBase schema, IEnumerable<Type> entityTypes)
    {
        _schema      = schema;
        _entityTypes = entityTypes;
    }

    // ── Factory methods ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="SchemaUpgrader"/> by scanning <paramref name="assembly"/> for
    /// all types decorated with <see cref="TableInfo"/> (the same filter applied by
    /// <see cref="DbInfoExtractor"/>). An optional <paramref name="filter"/> predicate
    /// can further restrict which types are included.
    /// </summary>
    public static SchemaUpgrader FromAssembly(
        DbConnection connection,
        DbProvider provider,
        Assembly assembly,
        Func<Type, bool>? filter = null)
    {
        var types = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<TableInfo>() is not null)
            .Where(filter ?? (_ => true))
            .ToList();

        return new SchemaUpgrader(CreateSchema(connection, provider), types);
    }

    /// <summary>
    /// Creates a <see cref="SchemaUpgrader"/> from an explicit collection of entity types.
    /// Types without a <see cref="TableInfo"/> attribute are accepted but will produce no
    /// schema output (they are filtered out inside <see cref="DbInfoExtractor"/>).
    /// </summary>
    public static SchemaUpgrader ForTypes(
        DbConnection connection,
        DbProvider provider,
        IEnumerable<Type> entityTypes)
        => new(CreateSchema(connection, provider), entityTypes);

    // ── Public operations ──────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the live database schema already matches
    /// the desired schema derived from the registered entity types.
    /// </summary>
    public async Task<bool> IsUpToDateAsync(CancellationToken ct = default)
    {
        var (diff, _, _) = await ComputeDiffAsync(ct);
        return diff.IsEmpty;
    }

    /// <summary>
    /// Applies all pending schema changes to the live database in a safe order:
    /// tables are created before columns are added to them; foreign keys are added
    /// after all referenced tables exist; constraints are dropped before the objects
    /// they reference are removed.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var (diff, _, _) = await ComputeDiffAsync(ct);
        if (diff.IsEmpty) return;

        foreach (var table in diff.TablesToCreate)
        {
            await _schema.CreateTableAsync(table, ct);
            foreach (var index in table.Indexes)
                await _schema.CreateIndexAsync(table.Name, table.Schema, index, ct);
        }

        foreach (var (tableName, tableSchema, col) in diff.ColumnsToAdd)
            await _schema.AddColumnAsync(tableName, tableSchema, col, ct);

        foreach (var (tableName, tableSchema, col) in diff.ColumnsToAlter)
            await _schema.AlterColumnAsync(tableName, tableSchema, col, ct);

        foreach (var (tableName, tableSchema, index) in diff.IndexesToCreate)
            await _schema.CreateIndexAsync(tableName, tableSchema, index, ct);

        foreach (var (tableName, tableSchema, fk) in diff.ForeignKeysToAdd)
            await _schema.AddForeignKeyAsync(tableName, tableSchema, fk, ct);

        foreach (var (tableName, tableSchema, fkName) in diff.ForeignKeysToDrop)
            await _schema.DropForeignKeyAsync(tableName, tableSchema, fkName, ct);

        foreach (var (tableName, tableSchema, indexName) in diff.IndexesToDrop)
            await _schema.DropIndexAsync(tableName, tableSchema, indexName, ct);

        foreach (var (tableName, tableSchema, colName) in diff.ColumnsToDrop)
            await _schema.DropColumnAsync(tableName, tableSchema, colName, ct);

        foreach (var (tableName, tableSchema) in diff.TablesToDrop)
            await _schema.DropTableAsync(tableName, tableSchema, ct);
    }

    /// <summary>
    /// Returns the ordered SQL statements required to migrate the live database to the
    /// desired schema. The statements are not executed — the caller can inspect, log, or
    /// run them manually.
    /// </summary>
    /// <returns>
    /// An empty list if the schema is already up to date; otherwise the statements in
    /// the order they must be executed.
    /// </returns>
    public async Task<IReadOnlyList<string>> ExportMigrationSqlAsync(CancellationToken ct = default)
    {
        var (diff, desired, actual) = await ComputeDiffAsync(ct);
        if (diff.IsEmpty) return [];
        return _schema.GenerateDiffSql(diff, desired, actual);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task<(SchemaDiff diff, SchemaSnapshot desired, SchemaSnapshot actual)> ComputeDiffAsync(
        CancellationToken ct)
    {
        var database = DbInfoExtractor.Extract(_entityTypes);
        var desired  = DbInfoSchemaConverter.Convert(database);
        var actual   = await _schema.GetCurrentSchemaAsync(ct);
        var diff     = SchemaDiffer.Compute(desired, actual);
        return (diff, desired, actual);
    }

    private static AnsiSqlSchemaBase CreateSchema(DbConnection connection, DbProvider provider) =>
        provider switch
        {
            DbProvider.SQLite     => new SqliteSchema(connection),
            DbProvider.SqlServer  => new SqlServerSchema(connection),
            _                     => throw new ArgumentOutOfRangeException(nameof(provider),
                                         provider, "Unknown DbProvider value."),
        };
}
