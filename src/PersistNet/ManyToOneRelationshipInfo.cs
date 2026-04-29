using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class ManyToOneRelationshipInfo : Attribute
{
    public string? Name { get; set; }

    public Type? RelatedType { get; set; }

    /// <summary>
    /// FK column(s) on this entity's table.
    /// </summary>
    public string[] FromKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// PK column(s) on the related table.
    /// </summary>
    public string[] ToKeys { get; set; } = Array.Empty<string>();

    public bool Nullable { get; set; } = false;

    public ReferentialRuleType OnDelete { get; set; } = ReferentialRuleType.Unspecified;

    public ReferentialRuleType OnUpdate { get; set; } = ReferentialRuleType.Unspecified;
}
