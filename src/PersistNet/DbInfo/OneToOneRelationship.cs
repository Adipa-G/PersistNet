using System;
using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class OneToOneRelationship : Relationship
{
    public string[] FromKeys { get; }
    public string[] ToKeys { get; }
    public string? MappedBy { get; }
    public bool Nullable { get; }
    public ReferentialRuleType? OnDelete { get; }
    public ReferentialRuleType? OnUpdate { get; }

    internal OneToOneRelationship(
        string? name,
        Type? relatedType,
        PropertyInfo property,
        string[] fromKeys,
        string[] toKeys,
        string? mappedBy,
        bool nullable,
        ReferentialRuleType? onDelete,
        ReferentialRuleType? onUpdate)
        : base(name, relatedType, property)
    {
        FromKeys = fromKeys;
        ToKeys = toKeys;
        MappedBy = mappedBy;
        Nullable = nullable;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }
}
