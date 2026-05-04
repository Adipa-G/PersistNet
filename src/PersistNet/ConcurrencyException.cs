using System;

namespace PersistNet;

/// <summary>
/// Thrown when an optimistic concurrency check fails during an UPDATE.
/// This indicates that another transaction has modified or deleted the row(s)
/// since they were originally read.
/// </summary>
public sealed class ConcurrencyException : InvalidOperationException
{
    /// <summary>The table on which the concurrency violation was detected.</summary>
    public string TableName { get; }

    /// <summary>Number of rows PersistNet expected to update.</summary>
    public int ExpectedRows { get; }

    /// <summary>Actual number of rows updated by the database.</summary>
    public int ActualRows { get; }

    internal ConcurrencyException(string tableName, int expectedRows, int actualRows)
        : base(
            $"Optimistic concurrency violation on '{tableName}': "
            + $"expected {expectedRows} row(s) updated, got {actualRows}. "
            + "Another transaction may have modified or deleted the row(s).")
    {
        TableName = tableName;
        ExpectedRows = expectedRows;
        ActualRows = actualRows;
    }
}
