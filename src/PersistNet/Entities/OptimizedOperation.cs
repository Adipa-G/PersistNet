using System;
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
    IReadOnlyList<IReadOnlyList<object?>> ValueRows,
    /// <summary>
    /// Parallel to <see cref="ValueRows"/>: a callback per row that receives the
    /// DB-generated key after INSERT, or <c>null</c> for rows that don't need hydration.
    /// When the entire list is <c>null</c>, no hydration is needed and the batch-insert
    /// path is used.
    /// </summary>
    IReadOnlyList<Action<object?>?>? KeyCallbacks = null,
    /// <summary>
    /// Name of the auto-increment primary-key column, when known.
    /// SQL Server uses this to emit <c>OUTPUT INSERTED.[keyCol]</c> and hydrate
    /// all generated keys in a single batch round trip instead of one per row.
    /// </summary>
    string? AutoIncrKeyColumn = null)
    : OptimizedOperation(TableName, Schema, OperationType.Insert);

/// <summary>
/// UPDATE operation where every affected row shares the same SET-clause values.
///
/// Two concurrency modes (at most one <c>Version*</c> field is non-null):
/// <list type="bullet">
///   <item>
///     <term>Homogeneous version</term>
///     <description>
///       All rows share the same expected version (<see cref="ExpectedVersionValue"/> is set).
///       SQL: <c>UPDATE t SET … WHERE keyCol IN (k1,k2,…) AND ver = @shared</c>
///     </description>
///   </item>
///   <item>
///     <term>Mixed versions</term>
///     <description>
///       Rows carry different expected versions (<see cref="ExpectedVersionValues"/> is set,
///       parallel to <see cref="KeyValues"/>).
///       SQL: <c>UPDATE t SET …, ver = ver+1 WHERE (key=@p AND ver=@p) OR …</c>
///       (SQL Server) or <c>(key,ver) IN (…)</c> (ANSI/SQLite).
///     </description>
///   </item>
/// </list>
/// </summary>
internal sealed record GroupedUpdate(
    string TableName,
    string? Schema,
    /// <summary>
    /// Columns and values for the SET clause — identical for every row in this group.
    /// Does <b>not</b> include the version column when <see cref="ExpectedVersionValues"/>
    /// is set; in that case the version increment is expressed as a computed SQL expression
    /// (<c>ver = ver + 1</c>) rather than a fixed parameter.
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
    /// Homogeneous-version mode: the single expected version shared by all rows in this group.
    /// Mutually exclusive with <see cref="ExpectedVersionValues"/>.
    /// </summary>
    object? ExpectedVersionValue = null,
    /// <summary>
    /// Mixed-version mode: one expected version per row, parallel to <see cref="KeyValues"/>.
    /// When set, the version increment is emitted as <c>ver = ver + 1</c> in the SET clause
    /// and the WHERE clause uses per-row <c>(key=@p AND ver=@p)</c> predicates.
    /// Mutually exclusive with <see cref="ExpectedVersionValue"/>.
    /// </summary>
    IReadOnlyList<object?>? ExpectedVersionValues = null)
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
