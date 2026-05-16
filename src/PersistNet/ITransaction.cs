using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PersistNet.Query;

namespace PersistNet;

public interface ITransaction : IAsyncDisposable
{
    void Save<T>(T entity);
    Task<T> SaveAndCommitAsync<T>(T entity);
    void Delete<T>(T entity);
    Task DeleteAndCommitAsync<T>(T entity);
    IEntityQuery<T> GetAsync<T>(params object[] keyValues) where T : class;
    Task CommitAsync();

    /// <summary>
    /// Returns a fluent query builder for <typeparamref name="T"/>.
    /// Chain <c>.Where()</c>, <c>.OrderBy()</c>, <c>.Take()</c> etc. and call a terminal
    /// method (<c>.ToListAsync()</c>, <c>.CountAsync()</c> …) to execute.
    /// </summary>
    ISelectQuery<T> Query<T>() where T : class, new();

    /// <summary>
    /// Executes a raw SQL query and materializes each result row into a new instance of
    /// <typeparamref name="T"/>.  Only properties decorated with <see cref="ColumnInfo"/>
    /// are mapped; extra result-set columns are silently ignored.
    /// Parameters can be supplied as an anonymous object (<c>new { Id = 1 }</c>),
    /// an <see cref="IDictionary{TKey,TValue}"/> of <c>string → object?</c>,
    /// or <c>null</c> for a parameterless query.
    /// Each property or key name is bound as <c>@Name</c>.
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken ct = default) where T : class, new();
}