using System;
using System.Reflection;

namespace PersistNet.DbInfo;

internal abstract class Relationship
{
    public string? Name { get; }
    public Type? RelatedType { get; }
    public PropertyInfo Property { get; }

    protected Relationship(string? name, Type? relatedType, PropertyInfo property)
    {
        Name = name;
        RelatedType = relatedType;
        Property = property;
    }
}
