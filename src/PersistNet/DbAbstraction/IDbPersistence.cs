using PersistNet.Entities;
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
    /// Reads a single entity of type <typeparamref name="T"/> by its primary key value.
    /// Returns <c>null</c> when no matching row exists.
    /// Composite-key entities are not yet supported.
    /// </summary>
    Task<T?> FindByKeyAsync<T>(object keyValue, CancellationToken ct = default) where T : class;
}
