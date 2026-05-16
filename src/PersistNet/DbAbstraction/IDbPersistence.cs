using PersistNet.DbInfo;
using PersistNet.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PersistNet.DbAbstraction;

/// <summary>
/// Abstracts provider-specific DML execution.  Each provider implements SQL
/// generation and parameter binding in its own subclass of
/// <see cref="AnsiSqlPersistenceBase"/>.
/// </summary>
internal interface IDbPersistence
{
    /// <summary>Executes an INSERT that covers one or more rows.</summary>
    Task ExecuteInsertAsync(MultiRowInsert insert, CancellationToken ct = default);

    /// <summary>Executes an UPDATE whose SET clause is shared across all target rows.</summary>
    Task ExecuteUpdateAsync(GroupedUpdate update, CancellationToken ct = default);

    /// <summary>Executes a DELETE that targets one or more rows by key.</summary>
    Task ExecuteDeleteAsync(BatchDelete delete, CancellationToken ct = default);

    /// <summary>
    /// Convenience dispatcher — routes an <see cref="OptimizedOperation"/> to the
    /// appropriate typed Execute method based on its runtime type.
    /// </summary>
    Task ExecuteAsync(OptimizedOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Reads a single entity of type <typeparamref name="T"/> by its primary key value(s).
    /// Returns <c>null</c> when no matching row exists.
    /// Pass values in <see cref="ColumnInfo.KeyOrder"/> sequence.
    /// </summary>
    Task<T?> FindByKeyAsync<T>(object[] keyValues, CancellationToken ct = default) where T : class;

    /// <summary>
    /// Loads the value of a single navigation property on <paramref name="entity"/> by
    /// executing the appropriate SQL for the relationship type.
    /// Returns a single related entity (for M2O / O2O) or a <see cref="System.Collections.IList"/>
    /// of the related entity type (for O2M / M2M), or <c>null</c> when nothing is found.
    /// </summary>
    Task<object?> LoadNavigationAsync(object entity, Table entityTable, Relationship relationship, CancellationToken ct = default);

    /// <summary>
    /// Batch-loads a navigation property for all <paramref name="entities"/> in a single
    /// SQL statement (using an IN clause), returning a <see cref="BatchNavResult"/> that maps
    /// each parent entity's lookup key to its loaded related value(s).
    /// Automatically chunks the IN values when they exceed the provider's
    /// <c>MaxParameterBatchSize</c>.
    /// </summary>
    Task<BatchNavResult> LoadNavigationBatchAsync(IReadOnlyList<object> entities, Table entityTable, Relationship relationship, CancellationToken ct = default);

    /// <summary>
    /// Executes a raw SQL query and materializes each row into a new instance of
    /// <typeparamref name="T"/>. Only properties decorated with <see cref="ColumnInfo"/>
    /// are mapped; extra result-set columns are silently ignored.
    /// </summary>
    Task<IReadOnlyList<T>> ExecuteQueryAsync<T>(
        string sql,
        List<(string Name, object? Value)> parameters,
        CancellationToken ct = default) where T : class, new();

    /// <summary>
    /// Executes a scalar query (e.g. <c>SELECT COUNT(*)</c>) and converts the
    /// result to <typeparamref name="TResult"/>.
    /// </summary>
    Task<TResult> ExecuteScalarAsync<TResult>(
        string sql,
        List<(string Name, object? Value)> parameters,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a scalar query and returns <c>null</c> when the DB value is
    /// <c>NULL</c> (e.g. <c>SELECT MAX(col)</c> on an empty table).
    /// </summary>
    Task<TResult?> ExecuteScalarNullableAsync<TResult>(
        string sql,
        List<(string Name, object? Value)> parameters,
        CancellationToken ct = default) where TResult : struct;

    /// <summary>
    /// Quotes a single SQL identifier using the provider-specific delimiter
    /// (ANSI double-quotes by default; SQL Server uses square brackets).
    /// </summary>
    string Quote(string identifier);

    /// <summary>
    /// Appends provider-specific LIMIT / OFFSET clauses to <paramref name="sql"/>.
    /// </summary>
    string AppendLimitOffset(string sql, int? skip, int? take);
}
