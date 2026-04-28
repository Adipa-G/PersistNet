using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class OneToManyRelationshipInfo : Attribute
{
    public string? Name { get; set; }

    public Type? RelatedType { get; set; }

    /// <summary>
    /// The property name on the many side (ManyToOneRelationshipInfo owner) that defines the FK.
    /// </summary>
    public string? MappedBy { get; set; }
}
