using System;
using System.Reflection;

namespace PersistNet.DbInfo;

internal sealed class OneToManyRelationship : Relationship
{
    public string? MappedBy { get; }

    internal OneToManyRelationship(
        string? name,
        Type? relatedType,
        PropertyInfo property,
        string? mappedBy)
        : base(name, relatedType, property)
    {
        MappedBy = mappedBy;
    }
}
