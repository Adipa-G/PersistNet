namespace PersistNet.DbAbstraction;

internal sealed class SchemaForeignKey
{
    public string? Name { get; }
    public string[] FromColumns { get; }
    public string ToTable { get; }
    public string? ToSchema { get; }
    public string[] ToColumns { get; }
    public ReferentialRuleType? OnDelete { get; }
    public ReferentialRuleType? OnUpdate { get; }

    internal SchemaForeignKey(
        string? name,
        string[] fromColumns,
        string toTable,
        string? toSchema,
        string[] toColumns,
        ReferentialRuleType? onDelete,
        ReferentialRuleType? onUpdate)
    {
        Name = name;
        FromColumns = fromColumns;
        ToTable = toTable;
        ToSchema = toSchema;
        ToColumns = toColumns;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }
}
