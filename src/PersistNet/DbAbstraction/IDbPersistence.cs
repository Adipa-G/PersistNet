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
}
