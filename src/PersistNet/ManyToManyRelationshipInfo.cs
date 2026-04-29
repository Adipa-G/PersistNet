using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Property)]
public class ManyToManyRelationshipInfo : Attribute
{
    public string? Name { get; set; }

    public Type? RelatedType { get; set; }

    /// <summary>
    /// Name of the generated join table. Required on the owning side.
    /// </summary>
    public string? JoinTableName { get; set; }

    public string? JoinTableSchema { get; set; }

    /// <summary>
    /// Column names in the join table referencing THIS entity's PK.
    /// </summary>
    public string[] LeftKeyColumns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Column names in the join table referencing the RELATED entity's PK.
    /// </summary>
    public string[] RightKeyColumns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Properties on THIS entity to use as FK source. Defaults to PK(s) if empty.
    /// </summary>
    public string[] LeftForeignKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Properties on the RELATED entity to use as FK source. Defaults to PK(s) if empty.
    /// </summary>
    public string[] RightForeignKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Set on the inverse side only � the property name on the owning side.
    /// </summary>
    public string? MappedBy { get; set; }

    public ReferentialRuleType OnDelete { get; set; } = ReferentialRuleType.Unspecified;

    public ReferentialRuleType OnUpdate { get; set; } = ReferentialRuleType.Unspecified;
}
