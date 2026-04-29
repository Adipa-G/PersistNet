namespace PersistNet.DbInfo;

internal sealed class Index
{
    public string? Name { get; }
    public string[] Columns { get; }
    public bool Unique { get; }

    internal Index(string? name, string[] columns, bool unique)
    {
        Name = name;
        Columns = columns;
        Unique = unique;
    }
}
