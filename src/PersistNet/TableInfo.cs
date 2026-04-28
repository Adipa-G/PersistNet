using System;

namespace PersistNet;

[AttributeUsage(AttributeTargets.Class)]
public class TableInfo : Attribute
{
    public string? TableName { get; set; }

    public string? Schema { get; set; }
}