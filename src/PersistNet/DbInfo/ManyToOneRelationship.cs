using System;
using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class ManyToOneRelationship : Relationship
{
    public string[] FromKeys { get; }
    public string[] ToKeys { get; }
    public bool Nullable { get; }
    public ReferentialRuleType? OnDelete { get; }
    public ReferentialRuleType? OnUpdate { get; }

    internal ManyToOneRelationship(
        string? name,
        Type? relatedType,
        PropertyInfo property,
        string[] fromKeys,
        string[] toKeys,
        bool nullable,
        ReferentialRuleType? onDelete,
        ReferentialRuleType? onUpdate)
        : base(name, relatedType, property)
    {
        FromKeys = fromKeys;
        ToKeys = toKeys;
        Nullable = nullable;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }
}
