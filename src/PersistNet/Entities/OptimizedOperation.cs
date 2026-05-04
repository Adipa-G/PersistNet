using System.Collections.Generic;

namespace PersistNet.Entities;

/// <summary>
/// A single (column, value) pair in an UPDATE SET clause.
/// </summary>
internal sealed record SetClause(string ColumnName, object? Value);

/// <summary>
/// Provider-agnostic representation of a single database operation that has been
/// optimised into a batch form.  The database abstraction layer converts these into
/// provider-specific parameterised SQL at commit time.
/// </summary>
internal abstract record OptimizedOperation(
    string TableName,
    string? Schema,
    OperationType OperationType);

/// <summary>
/// INSERT operation covering one or more rows.
/// Corresponds to:  INSERT INTO t (c1, c2, …) VALUES (r1v1, r1v2), (r2v1, r2v2), …
/// Whether the provider emits one multi-row statement or N individual statements is a
/// SQL-builder concern; this record is provider-agnostic.
/// </summary>
internal sealed record MultiRowInsert(
    string TableName,
    string? Schema,
    /// <summary>Ordered list of column names, consistent across all <see cref="ValueRows"/>.</summary>
    IReadOnlyList<string> Columns,
    /// <summary>One inner list per entity row; values are aligned to <see cref="Columns"/>.</summary>
    IReadOnlyList<IReadOnlyList<object?>> ValueRows)
    : OptimizedOperation(TableName, Schema, OperationType.Insert);

/// <summary>
/// UPDATE operation where every affected row shares the same SET-clause values.
/// Corresponds to:  UPDATE t SET c1=v1, c2=v2 WHERE keyCol IN (k1, k2, …)
/// When <see cref="VersionColumn"/> is set, the WHERE clause also includes
/// <c>AND VersionCol = <see cref="ExpectedVersionValue"/></c> for optimistic concurrency.
/// </summary>
internal sealed record GroupedUpdate(
    string TableName,
    string? Schema,
    /// <summary>Columns and values for the SET clause — identical for every row in this group.
    /// When a version column is present, the version SET clause has the incremented (new) value.
    /// </summary>
    IReadOnlyList<SetClause> SetClauses,
    /// <summary>Ordered key column names used in the WHERE clause.</summary>
    IReadOnlyList<string> KeyColumns,
    /// <summary>One inner list per entity row; values are aligned to <see cref="KeyColumns"/>.</summary>
    IReadOnlyList<IReadOnlyList<object?>> KeyValues,
    /// <summary>
    /// Name of the optimistic-concurrency version column, or <c>null</c> when the table has none.
    /// </summary>
    string? VersionColumn = null,
    /// <summary>
    /// The version value that must exist in the DB row (the value before this update).
    /// Rows in this group all share this expected version because they were grouped by fingerprint.
    /// </summary>
    object? ExpectedVersionValue = null)
    : OptimizedOperation(TableName, Schema, OperationType.Update);

/// <summary>
/// DELETE operation covering one or more rows.
/// Corresponds to:  DELETE FROM t WHERE keyCol IN (k1, k2, …)
/// For composite keys the SQL builder decides whether to use row-value constructors or
/// individual predicates, depending on the provider.
/// </summary>
internal sealed record BatchDelete(
    string TableName,
    string? Schema,
    /// <summary>Ordered key column names used in the WHERE clause.</summary>
    IReadOnlyList<string> KeyColumns,
    /// <summary>One inner list per entity row; values are aligned to <see cref="KeyColumns"/>.</summary>
    IReadOnlyList<IReadOnlyList<object?>> KeyValues)
    : OptimizedOperation(TableName, Schema, OperationType.Delete);
