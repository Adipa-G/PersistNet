using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PersistNet;

public sealed class TransactionFactory : ITransactionFactory
{
    private readonly string? _connectionString;
    private readonly DbProviderFactory? _providerFactory;
    private readonly DbConnection? _connection;
    private readonly ILogger<TransactionFactory> _logger;

    /// <summary>
    /// Connection string mode. A new connection is opened per transaction and returned to
    /// the ADO.NET pool when the transaction is committed or disposed.
    /// </summary>
    public TransactionFactory(
        string connectionString,
        DbProviderFactory providerFactory,
        ILogger<TransactionFactory> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = connectionString;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Direct connection mode. The provided connection is used as-is and its lifecycle
    /// is managed by the caller.
    /// </summary>
    public TransactionFactory(DbConnection connection, ILogger<TransactionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _logger = logger;
    }

    public async Task<ITransaction> OpenTransactionAsync()
    {
        if (_connection is not null)
        {
            _logger.LogDebug("Opening transaction on provided DbConnection.");
            var dbTransaction = await _connection.BeginTransactionAsync();
            return new Transaction(_connection, dbTransaction, ownsConnection: false, _logger);
        }
        else
        {
            _logger.LogDebug("Creating new DbConnection from connection string.");
            var conn = _providerFactory!.CreateConnection()
                ?? throw new InvalidOperationException("DbProviderFactory returned a null connection.");

            conn.ConnectionString = _connectionString;
            await conn.OpenAsync();

            var dbTransaction = await conn.BeginTransactionAsync();
            return new Transaction(conn, dbTransaction, ownsConnection: true, _logger);
        }
    }
}
