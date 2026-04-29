namespace PersistNet;

public enum ReferentialRuleType
{
    /// <summary>Not specified. Used as a sentinel default in attribute declarations.</summary>
    Unspecified = -1,
    Cascade,
    Restrict,
    DoNothing,
    SetNull,
}