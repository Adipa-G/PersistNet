using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PersistNet.DbInfo;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Integration tests for <see cref="ITransaction.QueryAsync{T}"/> — raw SQL queries
/// that materialise results into DTO types decorated with <see cref="ColumnInfo"/>.
/// Each test uses a fresh in-memory SQLite database.
/// </summary>
public sealed class DtoQueryTests : IAsyncDisposable
{
    private readonly SqliteConnection  _connection;
    private readonly TransactionFactory _factory;

    // ── DTO fixtures ────────────────────────────────────────────────────────

    private class PersonDto
    {
        [ColumnInfo(Key = true)] public int    Id   { get; set; }
        [ColumnInfo]             public string Name { get; set; } = "";
        [ColumnInfo]             public int    Age  { get; set; }
    }

    /// <summary>DTO whose "dept_id" column name differs from the property name.</summary>
    private class AliasDto
    {
        [ColumnInfo(Key = true)]                 public int Id     { get; set; }
        [ColumnInfo(ColumnName = "dept_id")]     public int DeptId { get; set; }
    }

    private class NullableDto
    {
        [ColumnInfo(Key = true)] public int  Id    { get; set; }
        [ColumnInfo]             public int? Score { get; set; }
    }

    // ── Setup / teardown ────────────────────────────────────────────────────

    public DtoQueryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _factory = new TransactionFactory(_connection, DbProvider.SQLite,
            NullLogger<TransactionFactory>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task ExecAsync(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SELECT * maps columns to properties by name (case-insensitive).
    /// </summary>
    [Fact]
    public async Task QueryAsync_ReturnsAllRows_MappedByPropertyName()
    {
        await ExecAsync("CREATE TABLE persons (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER)");
        await ExecAsync("INSERT INTO persons VALUES (1, 'Alice', 30)");
        await ExecAsync("INSERT INTO persons VALUES (2, 'Bob',   25)");

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<PersonDto>("SELECT * FROM persons ORDER BY Id");

        Assert.Equal(2, results.Count);
        Assert.Equal(1,       results[0].Id);
        Assert.Equal("Alice", results[0].Name);
        Assert.Equal(30,      results[0].Age);
        Assert.Equal(2,       results[1].Id);
        Assert.Equal("Bob",   results[1].Name);
    }

    /// <summary>
    /// A property with <c>[ColumnInfo(ColumnName = "dept_id")]</c> should be populated
    /// when the result set contains a column named <c>dept_id</c>.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithColumnInfoAlias_MapsCustomName()
    {
        await ExecAsync("CREATE TABLE alias_tbl (Id INTEGER PRIMARY KEY, dept_id INTEGER)");
        await ExecAsync("INSERT INTO alias_tbl VALUES (10, 99)");

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<AliasDto>("SELECT * FROM alias_tbl");

        Assert.Single(results);
        Assert.Equal(10, results[0].Id);
        Assert.Equal(99, results[0].DeptId);
    }

    /// <summary>
    /// Parameters supplied as an anonymous object are bound as <c>@PropertyName</c>.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithAnonymousObjectParameters_FiltersRows()
    {
        await ExecAsync("CREATE TABLE persons (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER)");
        await ExecAsync("INSERT INTO persons VALUES (1, 'Alice', 30)");
        await ExecAsync("INSERT INTO persons VALUES (2, 'Bob',   25)");

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<PersonDto>(
            "SELECT * FROM persons WHERE Id = @Id",
            new { Id = 2 });

        Assert.Single(results);
        Assert.Equal("Bob", results[0].Name);
    }

    /// <summary>
    /// A nullable property (<c>int?</c>) receives <c>null</c> when the database value is NULL.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithNullableProperty_MapsDbNullToNull()
    {
        await ExecAsync("CREATE TABLE scores (Id INTEGER PRIMARY KEY, Score INTEGER)");
        await ExecAsync("INSERT INTO scores VALUES (1, NULL)");

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<NullableDto>("SELECT * FROM scores");

        Assert.Single(results);
        Assert.Equal(1,    results[0].Id);
        Assert.Null(results[0].Score);
    }

    /// <summary>
    /// When the WHERE clause matches nothing the returned list is empty (not null).
    /// </summary>
    [Fact]
    public async Task QueryAsync_EmptyResult_ReturnsEmptyList()
    {
        await ExecAsync("CREATE TABLE persons (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER)");

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<PersonDto>("SELECT * FROM persons");

        Assert.Empty(results);
    }

    /// <summary>
    /// Parameters supplied as <see cref="IDictionary{TKey,TValue}"/> are bound correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithDictionaryParameters_FiltersRows()
    {
        await ExecAsync("CREATE TABLE persons (Id INTEGER PRIMARY KEY, Name TEXT, Age INTEGER)");
        await ExecAsync("INSERT INTO persons VALUES (1, 'Alice', 30)");
        await ExecAsync("INSERT INTO persons VALUES (2, 'Bob',   25)");

        var parameters = new Dictionary<string, object?> { ["Id"] = 1 };

        await using var txn = await _factory.OpenTransactionAsync();
        var results = await txn.QueryAsync<PersonDto>(
            "SELECT * FROM persons WHERE Id = @Id",
            parameters);

        Assert.Single(results);
        Assert.Equal("Alice", results[0].Name);
    }
}
