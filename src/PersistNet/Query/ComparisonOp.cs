namespace PersistNet.Query;

/// <summary>
/// SQL comparison operators available in the fluent expression builder.
/// </summary>
internal enum ComparisonOp
{
    Eq,
    Neq,
    Gt,
    Ge,
    Lt,
    Le,
    Like,
    Between,
    In,
}
