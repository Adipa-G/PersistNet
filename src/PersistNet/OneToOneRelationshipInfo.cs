using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class OneToOneRelationshipInfo : Attribute
{
    public string? Name { get; set; }

    public Type? RelatedType { get; set; }

    /// <summary>
    /// FK column(s) on this entity's table. Empty on the inverse side.
    /// </summary>
    public string[] FromKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// PK column(s) on the related table. Empty on the inverse side.
    /// </summary>
    public string[] ToKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Set on the inverse side only — the property name on the owning side.
    /// </summary>
    public string? MappedBy { get; set; }

    public bool Nullable { get; set; } = false;

    public ReferentialRuleType? OnDelete { get; set; }

    public ReferentialRuleType? OnUpdate { get; set; }
}
