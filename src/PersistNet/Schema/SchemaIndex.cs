namespace PersistNet.Schema;

internal sealed class SchemaIndex
{
    public string? Name { get; }
    public string[] Columns { get; }
    public bool IsUnique { get; }

    internal SchemaIndex(string? name, string[] columns, bool isUnique)
    {
        Name = name;
        Columns = columns;
        IsUnique = isUnique;
    }
}
