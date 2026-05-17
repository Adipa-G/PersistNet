using System;

namespace PersistNet;

/// <summary>
/// Pins the column resolution for a DTO projection property to a specific entity/table
/// type. Required when two or more joined tables share the same column name and the
/// compiler cannot determine which table to use unambiguously.
/// </summary>
/// <remarks>
/// <para>
/// <b>Basic usage</b> — force resolution to a specific table when both joined tables
/// have a column named <c>Id</c>:
/// <code>
/// [ColumnInfo(ColumnName = "CustomerId"), FromTable(typeof(Customer), ColumnName = "Id")]
/// public int CustomerId { get; set; }   // → SQL: t0."Id" AS "CustomerId"
/// </code>
/// </para>
/// <para>
/// <b><see cref="ColumnName"/> override</b> — use when the DB column to look up differs
/// from the DTO's <see cref="ColumnInfo.ColumnName"/> (or property name when
/// <see cref="ColumnInfo.ColumnName"/> is null). In the example above,
/// <c>FromTable.ColumnName = "Id"</c> tells the compiler to look for the DB column
/// <c>Id</c> in the Customer table, while <c>ColumnInfo.ColumnName = "CustomerId"</c>
/// becomes the SQL <c>AS</c> alias (and the key used by
/// <see cref="PersistNet.Mapping.DtoMapper"/> to match the reader field back to the
/// property).
/// </para>
/// <para>
/// When <see cref="ColumnName"/> is <c>null</c>, the column is looked up in the target
/// table using the value of <see cref="ColumnInfo.ColumnName"/> (or the property name
/// when that is also null).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FromTableAttribute : Attribute
{
    /// <summary>The entity type whose underlying table should be used to resolve this column.</summary>
    public Type TableType { get; }

    /// <summary>
    /// Optional DB column name to look up in the target table.
    /// When <c>null</c>, the lookup key is taken from <see cref="ColumnInfo.ColumnName"/>
    /// (or the property name if that is also <c>null</c>).
    /// </summary>
    public string? ColumnName { get; set; }

    /// <param name="tableType">
    /// The entity type whose mapped table will be searched for the column.
    /// Must be either the primary entity type or one of the joined entity types.
    /// </param>
    public FromTableAttribute(Type tableType) => TableType = tableType;
}
