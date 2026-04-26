using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PersistNet.Tests;

public class TransactionFactoryTests
{
    private const string InMemoryConnectionString = "Data Source=:memory:";

    // ── Connection string mode ─────────────────────────────────────────────

    [Fact]
    public async Task GivenConnectionStringMode_WhenOpenTransactionAsyncCalled_ThenReturnsTransaction()
    {
        var factory = new TransactionFactory(
            InMemoryConnectionString,
            SqliteFactory.Instance,
            NullLogger<TransactionFactory>.Instance);

        await using var transaction = await factory.OpenTransactionAsync();

        Assert.NotNull(transaction);
    }

    [Fact]
    public async Task GivenConnectionStringMode_WhenTransactionCommitted_ThenCanOpenAnotherTransaction()
    {
        var factory = new TransactionFactory(
            InMemoryConnectionString,
            SqliteFactory.Instance,
            NullLogger<TransactionFactory>.Instance);

        await using (var transaction = await factory.OpenTransactionAsync())
        {
            await transaction.CommitAsync();
        }

        // Pool still functional — can open another transaction
        await using var transaction2 = await factory.OpenTransactionAsync();
        Assert.NotNull(transaction2);
    }

    [Fact]
    public async Task GivenConnectionStringMode_WhenDisposedWithoutCommit_ThenRollsBackCleanly()
    {
        var factory = new TransactionFactory(
            InMemoryConnectionString,
            SqliteFactory.Instance,
            NullLogger<TransactionFactory>.Instance);

        // No commit — DisposeAsync should roll back cleanly
        await using var transaction = await factory.OpenTransactionAsync();
    }

    // ── Direct connection mode ─────────────────────────────────────────────

    [Fact]
    public async Task GivenDirectConnectionMode_WhenOpenTransactionAsyncCalled_ThenReturnsTransaction()
    {
        await using var conn = new SqliteConnection(InMemoryConnectionString);
        await conn.OpenAsync();

        var factory = new TransactionFactory(conn, NullLogger<TransactionFactory>.Instance);

        await using var transaction = await factory.OpenTransactionAsync();

        Assert.NotNull(transaction);
    }

    [Fact]
    public async Task GivenDirectConnectionMode_WhenTransactionCommitted_ThenConnectionRemainsOpen()
    {
        await using var conn = new SqliteConnection(InMemoryConnectionString);
        await conn.OpenAsync();

        var factory = new TransactionFactory(conn, NullLogger<TransactionFactory>.Instance);

        await using (var transaction = await factory.OpenTransactionAsync())
        {
            await transaction.CommitAsync();
        }

        // Caller owns the connection — must remain open after commit
        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact]
    public async Task GivenDirectConnectionMode_WhenDisposedWithoutCommit_ThenConnectionRemainsOpen()
    {
        await using var conn = new SqliteConnection(InMemoryConnectionString);
        await conn.OpenAsync();

        var factory = new TransactionFactory(conn, NullLogger<TransactionFactory>.Instance);

        await using (var transaction = await factory.OpenTransactionAsync())
        {
            // No commit — rollback on dispose
        }

        // Caller's connection must still be open
        Assert.Equal(ConnectionState.Open, conn.State);
    }

    // ── Guard conditions ───────────────────────────────────────────────────

    [Fact]
    public async Task GivenCommittedTransaction_WhenCommitCalledAgain_ThenThrowsInvalidOperationException()
    {
        var factory = new TransactionFactory(
            InMemoryConnectionString,
            SqliteFactory.Instance,
            NullLogger<TransactionFactory>.Instance);

        await using var transaction = await factory.OpenTransactionAsync();
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
    }

    [Fact]
    public async Task GivenDisposedTransaction_WhenCommitCalled_ThenThrowsObjectDisposedException()
    {
        var factory = new TransactionFactory(
            InMemoryConnectionString,
            SqliteFactory.Instance,
            NullLogger<TransactionFactory>.Instance);

        var transaction = await factory.OpenTransactionAsync();
        await transaction.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync());
    }

    [Fact]
    public void GivenConnectionStringMode_WhenNullConnectionString_ThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TransactionFactory(null!, SqliteFactory.Instance, NullLogger<TransactionFactory>.Instance));
    }

    [Fact]
    public void GivenConnectionStringMode_WhenNullProviderFactory_ThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TransactionFactory(InMemoryConnectionString, null!, NullLogger<TransactionFactory>.Instance));
    }

    [Fact]
    public void GivenDirectConnectionMode_WhenNullConnection_ThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TransactionFactory((System.Data.Common.DbConnection)null!, NullLogger<TransactionFactory>.Instance));
    }
}
