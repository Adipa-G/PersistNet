using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class SubTypeInfo : Attribute
{
    public Type EntityType { get; }

    public object DiscriminatorValue { get; }

    public SubTypeInfo(Type entityType, object discriminatorValue)
    {
        EntityType = entityType;
        DiscriminatorValue = discriminatorValue;
    }
}
