namespace PersistNet.Schema;

internal sealed class SchemaPrimaryKey
{
    public string? Name { get; }
    public string[] Columns { get; }

    internal SchemaPrimaryKey(string? name, string[] columns)
    {
        Name = name;
        Columns = columns;
    }
}
