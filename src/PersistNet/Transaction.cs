using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PersistNet;

public sealed class Transaction : ITransaction, IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _dbTransaction;
    private readonly bool _ownsConnection;
    private readonly ILogger _logger;
    private bool _committed;
    private bool _disposed;

    internal Transaction(
        DbConnection connection,
        DbTransaction dbTransaction,
        bool ownsConnection,
        ILogger logger)
    {
        _connection = connection;
        _dbTransaction = dbTransaction;
        _ownsConnection = ownsConnection;
        _logger = logger;
    }

    public async Task CommitAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_committed)
            throw new InvalidOperationException("Transaction has already been committed.");

        _logger.LogDebug("Committing transaction.");
        await _dbTransaction.CommitAsync();
        _committed = true;

        if (_ownsConnection)
        {
            _logger.LogDebug("Closing connection (returning to pool).");
            await _connection.CloseAsync();
        }
    }

    public void Save<T>(T entity) =>
        throw new NotImplementedException("SQL generation not yet implemented.");

    public Task<T> SaveAndFlushAsync<T>(T entity) =>
        throw new NotImplementedException("SQL generation not yet implemented.");

    public void Delete<T>(T entity) =>
        throw new NotImplementedException("SQL generation not yet implemented.");

    public Task DeleteAndFlushAsync<T>(T entity) =>
        throw new NotImplementedException("SQL generation not yet implemented.");

    public Task<T> GetAsync<T>(object id) =>
        throw new NotImplementedException("SQL generation not yet implemented.");

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
