using Microsoft.Extensions.Logging;
using PersistNet.DbInfo;
using PersistNet.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

/// <summary>
/// Abstract base that provides ANSI-SQL DML generation for INSERT, UPDATE, DELETE,
/// and single-key SELECT operations.
/// Subclasses override identifier quoting and composite-key WHERE-clause generation
/// where provider syntax diverges.
/// </summary>
internal abstract class AnsiSqlPersistenceBase : IDbPersistence
{
    protected DbConnection Connection { get; }
    private readonly DbTransaction? _dbTransaction;
    private readonly ILogger? _logger;

    protected AnsiSqlPersistenceBase(DbConnection connection, DbTransaction? transaction = null, ILogger? logger = null)
    {
        Connection = connection;
        _dbTransaction = transaction;
        _logger = logger;
    }

    // ── Parameter batch size (virtual — providers override for their own limits) ──────────────

    /// <summary>
    /// Maximum number of query parameters per statement.
    /// SQL Server hard-limit is 2100; 2000 is the safe default for both SQL Server and
    /// legacy SQLite (&lt; 3.32). <see cref="SqlitePersistence"/> overrides to 32000
    /// because <c>Microsoft.Data.Sqlite</c> 8.x bundles SQLite ≥ 3.32, whose
    /// <c>SQLITE_LIMIT_VARIABLE_NUMBER</c> is 32766.
    /// </summary>
    protected virtual int MaxParameterBatchSize => 2000;

    // ── Identifier quoting (virtual — SQL Server overrides with []) ─────────

    protected virtual string QuoteIdentifier(string name) => $"\"{name}\"";

    protected string QualifiedTable(string name, string? schema) =>
        schema is not null
            ? $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}"
            : QuoteIdentifier(name);

    // ── Command factory (──────────────────────────────────────────────────

    /// <summary>Creates a <see cref="DbCommand"/> pre-wired with this connection and
    /// the current ambient transaction. Use in provider overrides that need to
    /// issue auxiliary queries (e.g. <c>last_insert_rowid()</c>).</summary>
    protected DbCommand CreateCommand()
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = _dbTransaction;
        return cmd;
    }

    // ── Execution helper ────────────────────────────────────────────────────

    private async Task<int> RunAsync(string sql, List<(string Name, object? Value)> parameters, CancellationToken ct)
    {
        _logger?.LogDebug("Executing SQL: {Sql} | Params: {Params}", sql, FormatParams(parameters));
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _dbTransaction;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string FormatParams(List<(string Name, object? Value)> parameters)
        => parameters.Count == 0
            ? "(none)"
            : string.Join(", ", parameters.Select(p => $"{p.Name}={p.Value ?? "NULL"}"));

    // ── Dispatcher ──────────────────────────────────────────────────────────

    public Task ExecuteAsync(OptimizedOperation operation, CancellationToken ct = default) =>
        operation switch
        {
            MultiRowInsert insert => ExecuteInsertAsync(insert, ct),
            GroupedUpdate  update => ExecuteUpdateAsync(update, ct),
            BatchDelete    delete => ExecuteDeleteAsync(delete, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation),
                     $"Unknown OptimizedOperation type '{operation.GetType().Name}'.")
        };

    // ── INSERT ──────────────────────────────────────────────────────────────

    public async Task ExecuteInsertAsync(MultiRowInsert insert, CancellationToken ct = default)
    {
        if (insert.KeyCallbacks is null)
        {
            // No hydration needed — efficient batch path.
            var (sql, parameters) = BuildInsertSql(insert);
            await RunAsync(sql, parameters, ct);
            return;
        }

        // One or more rows need auto-increment PK hydration — insert per row.
        for (var i = 0; i < insert.ValueRows.Count; i++)
        {
            var singleRow = new MultiRowInsert(
                insert.TableName, insert.Schema, insert.Columns,
                new[] { insert.ValueRows[i] });
            var (sql, parameters) = BuildInsertSql(singleRow);
            await RunAsync(sql, parameters, ct);

            var callback = insert.KeyCallbacks[i];
            if (callback is not null)
            {
                var key = await GetLastInsertedKeyAsync(ct);
                if (key is not null)
                    callback(key);
            }
        }
    }

    /// <summary>
    /// Returns the key generated by the most recent INSERT on this connection.
    /// Override in provider subclasses to enable auto-increment PK hydration.
    /// Returns <c>null</c> in the base (hydration disabled for unknown providers).
    /// </summary>
    protected virtual Task<object?> GetLastInsertedKeyAsync(CancellationToken ct)
        => Task.FromResult<object?>(null);

    protected internal virtual (string Sql, List<(string Name, object? Value)> Parameters)
        BuildInsertSql(MultiRowInsert insert)
    {
        var parameters = new List<(string Name, object? Value)>();
        var idx = 0;

        var cols = string.Join(", ", insert.Columns.Select(QuoteIdentifier));
        var rowParts = new List<string>();

        foreach (var row in insert.ValueRows)
        {
            var pNames = new List<string>();
            foreach (var value in row)
            {
                var pName = $"@p{idx++}";
                parameters.Add((pName, value));
                pNames.Add(pName);
            }
            rowParts.Add($"({string.Join(", ", pNames)})");
        }

        var sql = $"INSERT INTO {QualifiedTable(insert.TableName, insert.Schema)} " +
                  $"({cols}) VALUES {string.Join(", ", rowParts)}";
        return (sql, parameters);
    }

    // ── UPDATE ──────────────────────────────────────────────────────────────

    public async Task ExecuteUpdateAsync(GroupedUpdate update, CancellationToken ct = default)
    {
        // Shared SET params + optional version param count; remainder is per-row key params.
        var sharedParams   = update.SetClauses.Count + (update.VersionColumn is not null ? 1 : 0);
        var paramsPerRow   = Math.Max(1, update.KeyColumns.Count);
        var maxRowsPerBatch = Math.Max(1, (MaxParameterBatchSize - sharedParams) / paramsPerRow);

        var expectedRows  = update.KeyValues.Count;
        var totalAffected = 0;

        foreach (var chunk in update.KeyValues.Chunk(maxRowsPerBatch))
        {
            var chunkUpdate = update with { KeyValues = chunk };
            var (sql, parameters) = BuildUpdateSql(chunkUpdate);
            totalAffected += await RunAsync(sql, parameters, ct);
        }

        if (totalAffected != expectedRows)
        {
            if (update.VersionColumn is not null)
                throw new ConcurrencyException(update.TableName, expectedRows, totalAffected);
            throw new InvalidOperationException(
                $"UPDATE on '{update.TableName}' expected {expectedRows} row(s) affected, "
                + $"but got {totalAffected}. The row(s) may have been deleted by another transaction.");
        }
    }

    protected internal virtual (string Sql, List<(string Name, object? Value)> Parameters)
        BuildUpdateSql(GroupedUpdate update)
    {
        var parameters = new List<(string Name, object? Value)>();
        var idx = 0;

        var setParts = new List<string>();
        foreach (var sc in update.SetClauses)
        {
            var pName = $"@p{idx++}";
            parameters.Add((pName, sc.Value));
            setParts.Add($"{QuoteIdentifier(sc.ColumnName)}={pName}");
        }

        var wherePart = BuildKeyWhereClause(update.KeyColumns, update.KeyValues, parameters, ref idx);

        if (update.VersionColumn is not null)
        {
            var pName = $"@p{idx++}";
            parameters.Add((pName, update.ExpectedVersionValue));
            wherePart += $" AND {QuoteIdentifier(update.VersionColumn)}={pName}";
        }

        var sql = $"UPDATE {QualifiedTable(update.TableName, update.Schema)} "
                + $"SET {string.Join(", ", setParts)} WHERE {wherePart}";
        return (sql, parameters);
    }

    // ── DELETE ──────────────────────────────────────────────────────────────

    public async Task ExecuteDeleteAsync(BatchDelete delete, CancellationToken ct = default)
    {
        var paramsPerRow    = Math.Max(1, delete.KeyColumns.Count);
        var maxRowsPerBatch = Math.Max(1, MaxParameterBatchSize / paramsPerRow);

        foreach (var chunk in delete.KeyValues.Chunk(maxRowsPerBatch))
        {
            var chunkDelete = delete with { KeyValues = chunk };
            var (sql, parameters) = BuildDeleteSql(chunkDelete);
            await RunAsync(sql, parameters, ct);
        }
    }

    protected internal virtual (string Sql, List<(string Name, object? Value)> Parameters)
        BuildDeleteSql(BatchDelete delete)
    {
        var parameters = new List<(string Name, object? Value)>();
        var idx = 0;

        var wherePart = BuildKeyWhereClause(delete.KeyColumns, delete.KeyValues, parameters, ref idx);
        var sql = $"DELETE FROM {QualifiedTable(delete.TableName, delete.Schema)} WHERE {wherePart}";
        return (sql, parameters);
    }

    // ── WHERE clause (virtual — SQL Server overrides composite-key handling) ─

    /// <summary>
    /// Generates the WHERE clause for key-based UPDATE and DELETE operations.
    /// Single-key:  <c>"col" IN (@p0, @p1, …)</c>
    /// Composite (ANSI/SQLite): <c>("k1","k2") IN ((@p0,@p1), (@p2,@p3))</c>
    /// SQL Server overrides composite to use OR-predicate chains.
    /// </summary>
    protected virtual string BuildKeyWhereClause(
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<IReadOnlyList<object?>> keyValues,
        List<(string Name, object? Value)> parameters,
        ref int idx)
    {
        if (keyColumns.Count == 1)
        {
            // Single key: straightforward IN clause.
            var col = QuoteIdentifier(keyColumns[0]);
            var pNames = new List<string>();
            foreach (var kv in keyValues)
            {
                var pName = $"@p{idx++}";
                parameters.Add((pName, kv[0]));
                pNames.Add(pName);
            }
            return $"{col} IN ({string.Join(", ", pNames)})";
        }

        // Composite key — ANSI row-value constructor.
        var colList = $"({string.Join(", ", keyColumns.Select(QuoteIdentifier))})";
        var rowTuples = new List<string>();
        foreach (var kv in keyValues)
        {
            var pNames = new List<string>();
            foreach (var value in kv)
            {
                var pName = $"@p{idx++}";
                parameters.Add((pName, value));
                pNames.Add(pName);
            }
            rowTuples.Add($"({string.Join(", ", pNames)})");
        }
        return $"{colList} IN ({string.Join(", ", rowTuples)})";
    }

    // ── SELECT / read ────────────────────────────────────────────────────────

    public async Task<T?> FindByKeyAsync<T>(object[] keyValues, CancellationToken ct = default) where T : class
    {
        var table = DbInfoCache.FindTable(typeof(T))
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' is not registered in DbInfoCache.");

        var keyCols = table.Columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrder).ToList();
        if (keyCols.Count != keyValues.Length)
            throw new ArgumentException(
                $"Table '{table.Name}' has {keyCols.Count} key column(s) but {keyValues.Length} value(s) were provided. " +
                $"Pass values in KeyOrder sequence.");

        // Joined subtype: entity data spans two tables — use a JOIN query to hydrate both.
        if (table.BaseTable != null)
            return await FindByKeyJoinedSubtypeAsync<T>(table, keyValues, ct);

        var subType = DbInfoCache.FindSubType(table, typeof(T));

        // Build SELECT column list: root columns + extra subtype columns when relevant.
        IEnumerable<Column> selectColumns = subType is null
            ? table.Columns
            : table.Columns.Concat(subType.ExtraColumns);

        var colList = string.Join(", ", selectColumns.Select(c => QuoteIdentifier(c.ColumnName)));
        var whereParts = keyCols.Select((col, i) => $"{QuoteIdentifier(col.ColumnName)} = @p{i}");
        var sql = $"SELECT {colList} FROM {QualifiedTable(table.Name, table.Schema)} " +
                  $"WHERE {string.Join(" AND ", whereParts)}";

        // Narrow to the correct subtype row when T is a registered subtype.
        var discriminatorCol = subType is not null
            ? table.Columns.FirstOrDefault(c => c.IsDiscriminator)
            : null;

        var discParamIndex = keyCols.Count;
        if (discriminatorCol is not null)
            sql += $" AND {QuoteIdentifier(discriminatorCol.ColumnName)} = @p{discParamIndex}";

        _logger?.LogDebug("Executing SQL: {Sql} | Keys: [{Keys}]{Disc}", sql,
            string.Join(", ", keyValues),
            discriminatorCol is not null ? $", @p{discParamIndex}={subType!.DiscriminatorValue}" : "");

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _dbTransaction;

        for (var i = 0; i < keyCols.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = keyValues[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        if (discriminatorCol is not null)
        {
            var discParam = cmd.CreateParameter();
            discParam.ParameterName = $"@p{discParamIndex}";
            discParam.Value = subType!.DiscriminatorValue;
            cmd.Parameters.Add(discParam);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return (T)MaterializeEntity(reader, typeof(T), selectColumns);
    }

    // ── Type coercion helper ─────────────────────────────────────────────────

    /// <summary>
    /// Executes a JOIN SELECT for a joined-subtype entity: base-table columns aliased as
    /// <c>b.*</c> plus the subtype's own columns aliased as <c>j.*</c> (PK excluded from
    /// the subtype side to avoid duplicate ambiguity).
    /// </summary>
    private async Task<T?> FindByKeyJoinedSubtypeAsync<T>(Table table, object[] keyValues, CancellationToken ct)
        where T : class
    {
        var baseTable = table.BaseTable!;
        var keyCols = baseTable.Columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrder).ToList();

        var baseColSelect = string.Join(", ",
            baseTable.Columns.Select(c => $"b.{QuoteIdentifier(c.ColumnName)}"));
        var joinColSelect = string.Join(", ",
            table.Columns.Where(c => !c.IsKey).Select(c => $"j.{QuoteIdentifier(c.ColumnName)}"));
        var colList = string.IsNullOrEmpty(joinColSelect)
            ? baseColSelect
            : $"{baseColSelect}, {joinColSelect}";

        var joinOn = string.Join(" AND ", keyCols.Select(c =>
            $"j.{QuoteIdentifier(c.ColumnName)} = b.{QuoteIdentifier(c.ColumnName)}"));
        var whereClause = string.Join(" AND ", keyCols.Select((c, i) =>
            $"b.{QuoteIdentifier(c.ColumnName)} = @p{i}"));

        var sql = $"SELECT {colList} " +
                  $"FROM {QualifiedTable(baseTable.Name, baseTable.Schema)} b " +
                  $"JOIN {QualifiedTable(table.Name, table.Schema)} j ON {joinOn} " +
                  $"WHERE {whereClause}";

        _logger?.LogDebug("Executing SQL (joined-subtype): {Sql} | Keys: [{Keys}]", sql,
            string.Join(", ", keyValues));

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _dbTransaction;

        for (var i = 0; i < keyCols.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = keyValues[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var selectColumns = baseTable.Columns
            .Concat(table.Columns.Where(c => !c.IsKey))
            .ToList();

        return (T)MaterializeEntity(reader, typeof(T), selectColumns);
    }

    // ── Navigation / eager loading ───────────────────────────────────────────

    public async Task<object?> LoadNavigationAsync(
        object entity, Table entityTable, Relationship relationship,
        CancellationToken ct = default)
    {
        if (relationship.RelatedType is null) return null;

        var relatedTable = DbInfoCache.FindTable(relationship.RelatedType);
        if (relatedTable is null) return null;

        switch (relationship)
        {
            case ManyToOneRelationship m2o:
            {
                var fkValues = m2o.FromKeys
                    .Select(k => FindColumn(entityTable, k)?.Getter(entity))
                    .ToArray();
                return await LoadSingleAsync(m2o.ToKeys, fkValues, relatedTable, ct);
            }

            case OneToOneRelationship o2o when o2o.MappedBy is null:
            {
                // Owning side: entity holds the FK.
                var fkValues = o2o.FromKeys
                    .Select(k => FindColumn(entityTable, k)?.Getter(entity))
                    .ToArray();
                return await LoadSingleAsync(o2o.ToKeys, fkValues, relatedTable, ct);
            }

            case OneToOneRelationship o2o:
            {
                // Inverse side: related entity holds FK pointing back to this entity's PK.
                var owningRel = relatedTable.Relationships
                    .OfType<OneToOneRelationship>()
                    .FirstOrDefault(r => r.Name == o2o.MappedBy);
                if (owningRel is null) return null;
                var pkValues = owningRel.ToKeys
                    .Select(k => FindColumn(entityTable, k)?.Getter(entity))
                    .ToArray();
                return await LoadSingleAsync(owningRel.FromKeys, pkValues, relatedTable, ct);
            }

            case OneToManyRelationship o2m:
            {
                var childM2o = relatedTable.Relationships
                    .OfType<ManyToOneRelationship>()
                    .FirstOrDefault(r => r.Name == o2m.MappedBy);
                if (childM2o is null) return null;
                var pkValues = childM2o.ToKeys
                    .Select(k => FindColumn(entityTable, k)?.Getter(entity))
                    .ToArray();
                return await LoadCollectionAsync(childM2o.FromKeys, pkValues, relatedTable, ct);
            }

            case ManyToManyRelationship m2m when m2m.MappedBy is null:
            {
                // Owning side.
                if (m2m.JoinTableName is null) return null;
                var leftFkCols = m2m.LeftForeignKeys.Length > 0
                    ? m2m.LeftForeignKeys.Select(k => FindColumn(entityTable, k)).ToArray()
                    : GetEffectiveKeyColumns(entityTable).Select(c => (Column?)c).ToArray();
                var leftValues = leftFkCols.Select(c => c?.Getter(entity)).ToArray();

                var rightFkNames = m2m.RightForeignKeys.Length > 0
                    ? m2m.RightForeignKeys
                    : GetEffectiveKeyColumns(relatedTable).Select(c => c.ColumnName).ToArray();

                var colList = string.Join(", ", relatedTable.Columns
                    .Select(c => $"r.{QuoteIdentifier(c.ColumnName)}"));
                var joinOn = string.Join(" AND ", m2m.RightKeyColumns
                    .Select((jc, i) => $"j.{QuoteIdentifier(jc)} = r.{QuoteIdentifier(rightFkNames[i])}"));
                var whereClause = string.Join(" AND ", m2m.LeftKeyColumns
                    .Select((jc, i) => $"j.{QuoteIdentifier(jc)} = @p{i}"));
                var sql = $"SELECT {colList} " +
                          $"FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} r " +
                          $"INNER JOIN {QualifiedTable(m2m.JoinTableName, m2m.JoinTableSchema)} j ON {joinOn} " +
                          $"WHERE {whereClause}";
                return await LoadCollectionWithSqlAsync(sql, leftValues, relatedTable, ct);
            }

            case ManyToManyRelationship m2m:
            {
                // Inverse side: find owning rel on related table and swap left/right.
                var owningM2m = relatedTable.Relationships
                    .OfType<ManyToManyRelationship>()
                    .FirstOrDefault(r => r.Name == m2m.MappedBy);
                if (owningM2m?.JoinTableName is null) return null;

                var rightFkCols = owningM2m.RightForeignKeys.Length > 0
                    ? owningM2m.RightForeignKeys.Select(k => FindColumn(entityTable, k)).ToArray()
                    : GetEffectiveKeyColumns(entityTable).Select(c => (Column?)c).ToArray();
                var rightValues = rightFkCols.Select(c => c?.Getter(entity)).ToArray();

                var leftFkNames = owningM2m.LeftForeignKeys.Length > 0
                    ? owningM2m.LeftForeignKeys
                    : GetEffectiveKeyColumns(relatedTable).Select(c => c.ColumnName).ToArray();

                var colList = string.Join(", ", relatedTable.Columns
                    .Select(c => $"r.{QuoteIdentifier(c.ColumnName)}"));
                var joinOn = string.Join(" AND ", owningM2m.LeftKeyColumns
                    .Select((jc, i) => $"j.{QuoteIdentifier(jc)} = r.{QuoteIdentifier(leftFkNames[i])}"));
                var whereClause = string.Join(" AND ", owningM2m.RightKeyColumns
                    .Select((jc, i) => $"j.{QuoteIdentifier(jc)} = @p{i}"));
                var sql = $"SELECT {colList} " +
                          $"FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} r " +
                          $"INNER JOIN {QualifiedTable(owningM2m.JoinTableName, owningM2m.JoinTableSchema)} j ON {joinOn} " +
                          $"WHERE {whereClause}";
                return await LoadCollectionWithSqlAsync(sql, rightValues, relatedTable, ct);
            }

            default:
                return null;
        }
    }

    private async Task<object?> LoadSingleAsync(
        string[] whereColNames, object?[] paramValues, Table relatedTable, CancellationToken ct)
    {
        var colList = string.Join(", ", relatedTable.Columns
            .Select(c => QuoteIdentifier(c.ColumnName)));
        var whereParts = whereColNames.Select((c, i) => $"{QuoteIdentifier(c)} = @p{i}");
        var sql = $"SELECT {colList} FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} " +
                  $"WHERE {string.Join(" AND ", whereParts)}";

        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < paramValues.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = paramValues[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MaterializeEntity(reader, relatedTable.EntityType, relatedTable.Columns);
    }

    private async Task<object?> LoadCollectionAsync(
        string[] whereColNames, object?[] paramValues, Table relatedTable, CancellationToken ct)
    {
        var colList = string.Join(", ", relatedTable.Columns
            .Select(c => QuoteIdentifier(c.ColumnName)));
        var whereParts = whereColNames.Select((c, i) => $"{QuoteIdentifier(c)} = @p{i}");
        var sql = $"SELECT {colList} FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} " +
                  $"WHERE {string.Join(" AND ", whereParts)}";

        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < paramValues.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = paramValues[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return await ReadCollectionAsync(cmd, relatedTable, ct);
    }

    private async Task<object?> LoadCollectionWithSqlAsync(
        string sql, object?[] paramValues, Table relatedTable, CancellationToken ct)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < paramValues.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = paramValues[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return await ReadCollectionAsync(cmd, relatedTable, ct);
    }

    private static async Task<object?> ReadCollectionAsync(
        DbCommand cmd, Table relatedTable, CancellationToken ct)
    {
        var listType = typeof(List<>).MakeGenericType(relatedTable.EntityType);
        var list = (IList)Activator.CreateInstance(listType)!;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MaterializeEntity(reader, relatedTable.EntityType, relatedTable.Columns));
        return list;
    }

    private static Column? FindColumn(Table table, string columnName)
    {
        var col = table.Columns.FirstOrDefault(c =>
            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (col is not null) return col;
        return table.BaseTable?.Columns.FirstOrDefault(c =>
            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<Column> GetEffectiveKeyColumns(Table table)
    {
        var t = table.BaseTable ?? table;
        return t.Columns.Where(c => c.IsKey).OrderBy(c => c.KeyOrder).ToList();
    }

    private static object MaterializeEntity(
        DbDataReader reader, Type entityType, IEnumerable<Column> columns)
    {
        var instance = Activator.CreateInstance(entityType)!;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            var col = columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));
            if (col is null) continue;
            var rawValue = reader.IsDBNull(i) ? null : reader.GetValue(i);
            col.Property.SetValue(instance, ConvertValue(rawValue, col.Property.PropertyType));
        }
        return instance;
    }

    // ── Batch navigation loading ─────────────────────────────────────────────

    public async Task<BatchNavResult> LoadNavigationBatchAsync(
        IReadOnlyList<object> entities,
        Table entityTable,
        Relationship relationship,
        CancellationToken ct = default)
    {
        if (entities.Count == 0 || relationship.RelatedType is null)
            return BatchNavResult.Empty;

        var relatedTable = DbInfoCache.FindTable(relationship.RelatedType);
        if (relatedTable is null) return BatchNavResult.Empty;

        switch (relationship)
        {
            case ManyToOneRelationship m2o:
                return await BatchLoadSingleAsync(
                    entities, entityTable, relatedTable,
                    entityFkNames: m2o.FromKeys, relatedPkNames: m2o.ToKeys, ct);

            case OneToOneRelationship o2o when o2o.MappedBy is null:
                return await BatchLoadSingleAsync(
                    entities, entityTable, relatedTable,
                    entityFkNames: o2o.FromKeys, relatedPkNames: o2o.ToKeys, ct);

            case OneToOneRelationship o2o:
            {
                var owningRel = relatedTable.Relationships.OfType<OneToOneRelationship>()
                    .FirstOrDefault(r => r.Name == o2o.MappedBy);
                if (owningRel is null) return BatchNavResult.Empty;
                return await BatchLoadInverseAsync(
                    entities, entityTable, relatedTable,
                    entityPkNames: owningRel.ToKeys, childFkNames: owningRel.FromKeys,
                    isCollection: false, ct);
            }

            case OneToManyRelationship o2m:
            {
                var childM2o = relatedTable.Relationships.OfType<ManyToOneRelationship>()
                    .FirstOrDefault(r => r.Name == o2m.MappedBy);
                if (childM2o is null) return BatchNavResult.Empty;
                return await BatchLoadInverseAsync(
                    entities, entityTable, relatedTable,
                    entityPkNames: childM2o.ToKeys, childFkNames: childM2o.FromKeys,
                    isCollection: true, ct);
            }

            case ManyToManyRelationship m2m when m2m.MappedBy is null:
            {
                if (m2m.JoinTableName is null) return BatchNavResult.Empty;
                var leftFkNames  = m2m.LeftForeignKeys.Length  > 0 ? m2m.LeftForeignKeys  : GetEffectiveKeyColumns(entityTable).Select(c => c.ColumnName).ToArray();
                var rightFkNames = m2m.RightForeignKeys.Length > 0 ? m2m.RightForeignKeys : GetEffectiveKeyColumns(relatedTable).Select(c => c.ColumnName).ToArray();
                return await BatchLoadM2MAsync(
                    entities, entityTable, relatedTable,
                    entityFkNames: leftFkNames,
                    joinTable: m2m.JoinTableName, joinSchema: m2m.JoinTableSchema,
                    joinEntityCols: m2m.LeftKeyColumns, joinRelatedCols: m2m.RightKeyColumns,
                    relatedPkNames: rightFkNames, ct);
            }

            case ManyToManyRelationship m2m:
            {
                var owningM2m = relatedTable.Relationships.OfType<ManyToManyRelationship>()
                    .FirstOrDefault(r => r.Name == m2m.MappedBy);
                if (owningM2m?.JoinTableName is null) return BatchNavResult.Empty;
                var entityFkNames  = owningM2m.RightForeignKeys.Length > 0 ? owningM2m.RightForeignKeys : GetEffectiveKeyColumns(entityTable).Select(c => c.ColumnName).ToArray();
                var relatedFkNames = owningM2m.LeftForeignKeys.Length  > 0 ? owningM2m.LeftForeignKeys  : GetEffectiveKeyColumns(relatedTable).Select(c => c.ColumnName).ToArray();
                // Swap left ↔ right relative to the owning side.
                return await BatchLoadM2MAsync(
                    entities, entityTable, relatedTable,
                    entityFkNames: entityFkNames,
                    joinTable: owningM2m.JoinTableName, joinSchema: owningM2m.JoinTableSchema,
                    joinEntityCols: owningM2m.RightKeyColumns, joinRelatedCols: owningM2m.LeftKeyColumns,
                    relatedPkNames: relatedFkNames, ct);
            }

            default:
                return BatchNavResult.Empty;
        }
    }

    /// <summary>
    /// M2O / O2O-owning: entity holds FK → load one parent per distinct FK value.
    /// Returns dictionary: entity-FK-string → parent entity.
    /// </summary>
    private async Task<BatchNavResult> BatchLoadSingleAsync(
        IReadOnlyList<object> entities,
        Table entityTable,
        Table relatedTable,
        string[] entityFkNames,
        string[] relatedPkNames,
        CancellationToken ct)
    {
        var (distinctInValues, entityKeySelector) = BuildInValues(entities, entityTable, entityFkNames);
        if (distinctInValues.Count == 0)
            return new BatchNavResult { Entries = new Dictionary<string, object?>(), EntityKeySelector = entityKeySelector };

        var colList      = string.Join(", ", relatedTable.Columns.Select(c => QuoteIdentifier(c.ColumnName)));
        var paramsPerRow = Math.Max(1, relatedPkNames.Length);
        var maxRows      = Math.Max(1, MaxParameterBatchSize / paramsPerRow);
        var entries      = new Dictionary<string, object?>();

        foreach (var chunk in distinctInValues.Chunk(maxRows))
        {
            var parameters = new List<(string Name, object? Value)>();
            var idx        = 0;
            var where      = BuildKeyWhereClause(relatedPkNames, chunk, parameters, ref idx);
            var sql        = $"SELECT {colList} FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} WHERE {where}";

            using var cmd = CreateCommand();
            cmd.CommandText = sql;
            BindParameters(cmd, parameters);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var loaded = MaterializeEntity(reader, relatedTable.EntityType, relatedTable.Columns);
                var key    = MakeKey(relatedPkNames.Select(k => FindColumn(relatedTable, k)?.Getter(loaded)));
                entries[key] = loaded;
            }
        }

        return new BatchNavResult { Entries = entries, EntityKeySelector = entityKeySelector };
    }

    /// <summary>
    /// O2O-inverse / O2M: entity PK → children whose FK points back.
    /// Returns dictionary: entity-PK-string → single child or List&lt;RelatedType&gt;.
    /// </summary>
    private async Task<BatchNavResult> BatchLoadInverseAsync(
        IReadOnlyList<object> entities,
        Table entityTable,
        Table relatedTable,
        string[] entityPkNames,
        string[] childFkNames,
        bool isCollection,
        CancellationToken ct)
    {
        var (distinctInValues, entityKeySelector) = BuildInValues(entities, entityTable, entityPkNames);
        if (distinctInValues.Count == 0)
            return new BatchNavResult { Entries = new Dictionary<string, object?>(), EntityKeySelector = entityKeySelector };

        var listType     = isCollection ? typeof(List<>).MakeGenericType(relatedTable.EntityType) : null;
        var colList      = string.Join(", ", relatedTable.Columns.Select(c => QuoteIdentifier(c.ColumnName)));
        var paramsPerRow = Math.Max(1, childFkNames.Length);
        var maxRows      = Math.Max(1, MaxParameterBatchSize / paramsPerRow);
        var entries      = new Dictionary<string, object?>();

        // Pre-populate empty collections for every entity key so missing children return [].
        if (isCollection)
            foreach (var vals in distinctInValues)
                entries[MakeKey(vals.Cast<object?>())] = Activator.CreateInstance(listType!)!;

        foreach (var chunk in distinctInValues.Chunk(maxRows))
        {
            var parameters = new List<(string Name, object? Value)>();
            var idx        = 0;
            var where      = BuildKeyWhereClause(childFkNames, chunk, parameters, ref idx);
            var sql        = $"SELECT {colList} FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} WHERE {where}";

            using var cmd = CreateCommand();
            cmd.CommandText = sql;
            BindParameters(cmd, parameters);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var loaded = MaterializeEntity(reader, relatedTable.EntityType, relatedTable.Columns);
                var key    = MakeKey(childFkNames.Select(k => FindColumn(relatedTable, k)?.Getter(loaded)));

                if (isCollection)
                {
                    if (!entries.TryGetValue(key, out var existing))
                        entries[key] = existing = Activator.CreateInstance(listType!)!;
                    ((IList)existing!).Add(loaded);
                }
                else
                {
                    entries[key] = loaded;
                }
            }
        }

        return new BatchNavResult { Entries = entries, EntityKeySelector = entityKeySelector };
    }

    /// <summary>
    /// M2M owning or inverse: entity FK → related entities via join table.
    /// Includes the join key as <c>__gk_N</c> alias columns for grouping.
    /// Returns dictionary: entity-FK-string → List&lt;RelatedType&gt;.
    /// </summary>
    private async Task<BatchNavResult> BatchLoadM2MAsync(
        IReadOnlyList<object> entities,
        Table entityTable,
        Table relatedTable,
        string[] entityFkNames,
        string  joinTable,
        string? joinSchema,
        string[] joinEntityCols,
        string[] joinRelatedCols,
        string[] relatedPkNames,
        CancellationToken ct)
    {
        var (distinctInValues, entityKeySelector) = BuildInValues(entities, entityTable, entityFkNames);
        if (distinctInValues.Count == 0)
            return new BatchNavResult { Entries = new Dictionary<string, object?>(), EntityKeySelector = entityKeySelector };

        var listType       = typeof(List<>).MakeGenericType(relatedTable.EntityType);
        var gkAliases      = joinEntityCols.Select((_, i) => $"__gk_{i}").ToArray();
        var relColList     = string.Join(", ", relatedTable.Columns.Select(c => $"r.{QuoteIdentifier(c.ColumnName)}"));
        var gkSelect       = string.Join(", ", joinEntityCols.Select((jc, i) => $"j.{QuoteIdentifier(jc)} AS {gkAliases[i]}"));
        var joinOn         = string.Join(" AND ", joinRelatedCols.Select((jc, i) => $"j.{QuoteIdentifier(jc)} = r.{QuoteIdentifier(relatedPkNames[i])}"));
        var paramsPerRow   = Math.Max(1, joinEntityCols.Length);
        var maxRows        = Math.Max(1, MaxParameterBatchSize / paramsPerRow);
        var entries        = new Dictionary<string, object?>();
        var loadedByPk     = new Dictionary<string, object>(); // dedup related entities by PK

        // Pre-populate empty lists.
        foreach (var vals in distinctInValues)
            entries[MakeKey(vals.Cast<object?>())] = Activator.CreateInstance(listType)!;

        foreach (var chunk in distinctInValues.Chunk(maxRows))
        {
            var parameters = new List<(string Name, object? Value)>();
            var idx        = 0;
            var where      = BuildAliasedWhereClause("j", joinEntityCols, chunk, parameters, ref idx);
            var sql        = $"SELECT {relColList}, {gkSelect} " +
                             $"FROM {QualifiedTable(relatedTable.Name, relatedTable.Schema)} r " +
                             $"INNER JOIN {QualifiedTable(joinTable, joinSchema)} j ON {joinOn} " +
                             $"WHERE {where}";

            using var cmd = CreateCommand();
            cmd.CommandText = sql;
            BindParameters(cmd, parameters);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Read join-table group-key values.
                var groupKey = string.Join(":", gkAliases.Select(alias =>
                {
                    var ord = reader.GetOrdinal(alias);
                    return reader.IsDBNull(ord) ? "null" : reader.GetValue(ord)?.ToString() ?? "null";
                }));

                // Materialize; deduplicate by related PK to share instances.
                var loaded = MaterializeEntity(reader, relatedTable.EntityType, relatedTable.Columns);
                var relPk  = MakeKey(GetEffectiveKeyColumns(relatedTable).Select(c => c.Getter(loaded)));
                if (!loadedByPk.TryGetValue(relPk, out var canonical))
                    loadedByPk[relPk] = canonical = loaded;

                if (entries.TryGetValue(groupKey, out var list))
                    ((IList)list!).Add(canonical);
            }
        }

        return new BatchNavResult { Entries = entries, EntityKeySelector = entityKeySelector };
    }

    // ── Batch helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Collects distinct IN values from entities for the given column names,
    /// and returns a pre-compiled entity-key selector function.
    /// </summary>
    private (List<IReadOnlyList<object?>> InValues, Func<object, string?> KeySelector)
        BuildInValues(IReadOnlyList<object> entities, Table entityTable, string[] columnNames)
    {
        var cols = columnNames.Select(k => FindColumn(entityTable, k)).ToArray();
        var seen = new HashSet<string>();
        var inValues = new List<IReadOnlyList<object?>>();

        foreach (var entity in entities)
        {
            var vals = cols.Select(c => c?.Getter(entity)).ToList();
            if (vals.Any(v => v is null)) continue;
            var key = MakeKey(vals.Cast<object?>());
            if (seen.Add(key))
                inValues.Add(vals!);
        }

        Func<object, string?> selector = entity =>
        {
            var parts = new string?[cols.Length];
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i] is null) return null;
                var v = cols[i]!.Getter(entity);
                if (v is null) return null;
                parts[i] = v.ToString();
            }
            return string.Join(":", parts);
        };

        return (inValues, selector);
    }

    /// <summary>Builds a WHERE clause for table-aliased columns (e.g. <c>j.ActorId IN (...)</c>).
    /// Uses IN for single-column keys; OR-chain for composite (portable across providers).
    /// </summary>
    private string BuildAliasedWhereClause(
        string alias,
        string[] cols,
        IReadOnlyList<IReadOnlyList<object?>> vals,
        List<(string Name, object? Value)> parameters,
        ref int idx)
    {
        if (cols.Length == 1)
        {
            var col   = $"{alias}.{QuoteIdentifier(cols[0])}";
            var pNames = new List<string>();
            foreach (var v in vals)
            {
                var pn = $"@p{idx++}";
                parameters.Add((pn, v[0]));
                pNames.Add(pn);
            }
            return $"{col} IN ({string.Join(", ", pNames)})";
        }

        var predicates = new List<string>();
        foreach (var v in vals)
        {
            var andParts = new List<string>();
            for (var i = 0; i < cols.Length; i++)
            {
                var pn = $"@p{idx++}";
                parameters.Add((pn, v[i]));
                andParts.Add($"{alias}.{QuoteIdentifier(cols[i])} = {pn}");
            }
            predicates.Add($"({string.Join(" AND ", andParts)})");
        }
        return $"({string.Join(" OR ", predicates)})";
    }

    private static string MakeKey(IEnumerable<object?> values)
        => string.Join(":", values.Select(v => v?.ToString() ?? "null"));

    private static void BindParameters(DbCommand cmd, List<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    /// <summary>
    /// Coerces a value read from <see cref="DbDataReader"/> to the CLR property type.
    /// Handles the common case where SQLite returns <c>long</c> for integer columns,
    /// <c>double</c> for decimal columns, etc.
    /// </summary>
    private static object? ConvertValue(object? dbValue, Type targetType)
    {
        if (dbValue is null) return null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsAssignableFrom(dbValue.GetType())) return dbValue;
        return Convert.ChangeType(dbValue, underlying);
    }
}
