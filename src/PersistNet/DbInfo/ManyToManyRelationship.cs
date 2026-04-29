using System;
using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class ManyToManyRelationship : Relationship
{
    public string? JoinTableName { get; }
    public string? JoinTableSchema { get; }
    public string[] LeftKeyColumns { get; }
    public string[] RightKeyColumns { get; }
    public string[] LeftForeignKeys { get; }
    public string[] RightForeignKeys { get; }
    public string? MappedBy { get; }
    public ReferentialRuleType? OnDelete { get; }
    public ReferentialRuleType? OnUpdate { get; }

    internal ManyToManyRelationship(
        string? name,
        Type? relatedType,
        PropertyInfo property,
        string? joinTableName,
        string? joinTableSchema,
        string[] leftKeyColumns,
        string[] rightKeyColumns,
        string[] leftForeignKeys,
        string[] rightForeignKeys,
        string? mappedBy,
        ReferentialRuleType? onDelete,
        ReferentialRuleType? onUpdate)
        : base(name, relatedType, property)
    {
        JoinTableName = joinTableName;
        JoinTableSchema = joinTableSchema;
        LeftKeyColumns = leftKeyColumns;
        RightKeyColumns = rightKeyColumns;
        LeftForeignKeys = leftForeignKeys;
        RightForeignKeys = rightForeignKeys;
        MappedBy = mappedBy;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }
}
