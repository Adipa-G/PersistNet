using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;
using PersistNet.Entities;
using PersistNet.Entities.VirtualDb;

namespace PersistNet;

public sealed class Transaction : ITransaction, IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _dbTransaction;
    private readonly bool _ownsConnection;
    private readonly ILogger _logger;
    private readonly ChangeSetBuilder _changeSetBuilder = new();
    private readonly IDbPersistence _persistence;
    private bool _committed;
    private bool _disposed;

    internal Transaction(
        DbConnection connection,
        DbTransaction dbTransaction,
        bool ownsConnection,
        DbProvider provider,
        ILogger logger)
    {
        _connection = connection;
        _dbTransaction = dbTransaction;
        _ownsConnection = ownsConnection;
        _logger = logger;
        _persistence = provider switch
        {
            DbProvider.SQLite    => new SqlitePersistence(connection, dbTransaction, _logger),
            DbProvider.SqlServer => new SqlServerPersistence(connection, dbTransaction, _logger),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider,
                     "Unknown DbProvider value.")
        };
    }

    public async Task CommitAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_committed)
            throw new InvalidOperationException("Transaction has already been committed.");

        // Flush pending Save/Delete operations before committing.
        var batches = _changeSetBuilder.GetOrderedBatches();
        if (batches.Count > 0)
        {
            _logger.LogDebug("Flushing {Count} pending VTable batch(es).", batches.Count);
            foreach (var vtable in batches)
                foreach (var op in StatementOptimizer.Optimize(vtable))
                    await _persistence.ExecuteAsync(op);
        }

        _logger.LogDebug("Committing transaction.");
        await _dbTransaction.CommitAsync();
        _committed = true;

        if (_ownsConnection)
        {
            _logger.LogDebug("Closing connection (returning to pool).");
            await _connection.CloseAsync();
        }
    }

    public void Save<T>(T entity) => _changeSetBuilder.Save(entity!);

    public async Task<T> SaveAndCommitAsync<T>(T entity)
    {
        Save(entity);
        await CommitAsync();
        return entity;
    }

    public void Delete<T>(T entity) => _changeSetBuilder.Delete(entity!);

    public async Task DeleteAndCommitAsync<T>(T entity)
    {
        Delete(entity);
        await CommitAsync();
    }

    public IEntityQuery<T> GetAsync<T>(params object[] keyValues) where T : class
        => new EntityQuery<T>(this, keyValues);

    internal async Task<T> LoadEntityCoreAsync<T>(object[] keyValues) where T : class
    {
        var result = await _persistence.FindByKeyAsync<T>(keyValues);
        if (result is null) throw new InvalidOperationException(
            $"No {typeof(T).Name} with key ({string.Join(", ", keyValues)}) was found.");

        _changeSetBuilder.TrackSnapshot(result);
        return result;
    }

    internal IReadOnlyList<string> GetAllRelationshipNames(Type type)
    {
        var table = DbInfoCache.FindTable(type);
        if (table is null) return Array.Empty<string>();
        return table.Relationships
            .Where(r => r.Name is not null)
            .Select(r => r.Name!)
            .ToList();
    }

    internal async Task LoadEntityGraphAsync(object entity, IReadOnlyList<string> includes, HashSet<string> visited)
    {
        var table = DbInfoCache.FindTable(entity.GetType());
        if (table is null) return;

        // Build a visited key from this entity's PK values to detect cycles.
        var keyTable = table.BaseTable ?? table;
        var pkStr = string.Join(":", keyTable.Columns
            .Where(c => c.IsKey)
            .OrderBy(c => c.KeyOrder)
            .Select(c => c.Getter(entity)?.ToString() ?? "null"));
        var visitedKey = $"{entity.GetType().FullName}:{pkStr}";
        if (!visited.Add(visitedKey)) return;

        foreach (var propName in includes)
        {
            var rel = table.Relationships.FirstOrDefault(r => r.Name == propName);
            if (rel is null) continue;

            var rawValue = await _persistence.LoadNavigationAsync(entity, table, rel);
            if (rawValue is null) continue;

            rel.Property.SetValue(entity, rawValue);

            if (rawValue is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is null) continue;
                    _changeSetBuilder.TrackSnapshot(item);
                    await RecurseIntoRelatedAsync(item, visited);
                }
            }
            else
            {
                _changeSetBuilder.TrackSnapshot(rawValue);
                await RecurseIntoRelatedAsync(rawValue, visited);
            }
        }
    }

    private async Task RecurseIntoRelatedAsync(object entity, HashSet<string> visited)
    {
        var relatedTable = DbInfoCache.FindTable(entity.GetType());
        if (relatedTable is null) return;
        var relIncludes = relatedTable.Relationships
            .Where(r => r.Name is not null)
            .Select(r => r.Name!)
            .ToList();
        if (relIncludes.Count > 0)
            await LoadEntityGraphAsync(entity, relIncludes, visited);
    }

    /// <summary>
    /// Batch version of <see cref="LoadEntityGraphAsync"/>: loads all navigation
    /// properties for every entity in <paramref name="entities"/> using IN-clause SQL
    /// instead of per-entity queries, eliminating the N+1 problem when the caller
    /// already has a list of entities to fully hydrate.
    /// </summary>
    internal async Task LoadEntityGraphBatchAsync(
        IReadOnlyList<object> entities,
        IReadOnlyList<string> includes,
        HashSet<string> visited)
    {
        if (entities.Count == 0) return;

        var entityType = entities[0].GetType();
        var table = DbInfoCache.FindTable(entityType);
        if (table is null) return;

        // Filter to only entities not yet visited — mirrors the per-entity early-return
        // in LoadEntityGraphAsync and is what breaks cycles in the batch path.
        var unvisited = new List<object>(entities.Count);
        foreach (var entity in entities)
        {
            var keyTable = table.BaseTable ?? table;
            var pkStr = string.Join(":", keyTable.Columns
                .Where(c => c.IsKey)
                .OrderBy(c => c.KeyOrder)
                .Select(c => c.Getter(entity)?.ToString() ?? "null"));
            if (visited.Add($"{entityType.FullName}:{pkStr}"))
                unvisited.Add(entity);
        }
        if (unvisited.Count == 0) return;

        foreach (var propName in includes)
        {
            var rel = table.Relationships.FirstOrDefault(r => r.Name == propName);
            if (rel is null) continue;

            var batchResult = await _persistence.LoadNavigationBatchAsync(unvisited, table, rel, default);

            var nextLevelEntities = new List<object>();

            foreach (var entity in unvisited)
            {
                var lookupKey = batchResult.EntityKeySelector(entity);
                if (lookupKey is null) continue;
                if (!batchResult.Entries.TryGetValue(lookupKey, out var rawValue) || rawValue is null) continue;

                rel.Property.SetValue(entity, rawValue);

                if (rawValue is System.Collections.IEnumerable enumerable and not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is null) continue;
                        _changeSetBuilder.TrackSnapshot(item);
                        nextLevelEntities.Add(item);
                    }
                }
                else
                {
                    _changeSetBuilder.TrackSnapshot(rawValue);
                    nextLevelEntities.Add(rawValue);
                }
            }

            // Recurse into the next level — batch if multiple entities of same type,
            // fall through to single-entity path for already-visited nodes (skipped
            // inside LoadEntityGraphBatchAsync via the visited set).
            if (nextLevelEntities.Count > 0)
            {
                var groups = nextLevelEntities
                    .GroupBy(e => e.GetType())
                    .ToList();

                foreach (var group in groups)
                {
                    var groupList = group.ToList();
                    var relatedTable = DbInfoCache.FindTable(group.Key);
                    if (relatedTable is null) continue;

                    var relIncludes = relatedTable.Relationships
                        .Where(r => r.Name is not null)
                        .Select(r => r.Name!)
                        .ToList();

                    if (relIncludes.Count > 0)
                        await LoadEntityGraphBatchAsync(groupList, relIncludes, visited);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_committed)
        {
            _logger.LogDebug("Rolling back uncommitted transaction.");
            await _dbTransaction.RollbackAsync();
        }

        await _dbTransaction.DisposeAsync();

        if (_ownsConnection)
        {
            await _connection.DisposeAsync();
        }
    }
}
