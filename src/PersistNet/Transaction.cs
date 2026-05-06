using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PersistNet.DbAbstraction;
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

    public async Task<T> GetAsync<T>(object id) where T : class
    {
        var result = await _persistence.FindByKeyAsync<T>(id);
        if (result is null) throw new InvalidOperationException(
            $"No {typeof(T).Name} with key '{id}' was found.");

        // Snapshot the loaded state so that a later Save() can omit unchanged columns.
        _changeSetBuilder.TrackSnapshot(result);
        return result;
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
