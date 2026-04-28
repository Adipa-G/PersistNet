using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class IndexInfo : Attribute
{
    public string? Name { get; set; }

    public string[] Columns { get; set; } = Array.Empty<string>();

    public bool Unique { get; set; } = false;
}
