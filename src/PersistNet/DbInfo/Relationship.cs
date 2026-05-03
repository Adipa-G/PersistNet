using System;
using System.Linq.Expressions;
using System.Reflection;

namespace PersistNet.DbInfo;

internal abstract class Relationship
{
    public string? Name { get; }
    public Type? RelatedType { get; }
    public PropertyInfo Property { get; }

    /// <summary>Compiled getter — avoids reflection overhead on every traversal.</summary>
    public Func<object, object?> Getter { get; }

    protected Relationship(string? name, Type? relatedType, PropertyInfo property)
    {
        Name = name;
        RelatedType = relatedType;
        Property = property;
        Getter = BuildGetter(property);
    }

    private static Func<object, object?> BuildGetter(PropertyInfo property)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var cast = Expression.Convert(param, property.DeclaringType!);
        var access = Expression.Property(cast, property);
        var box = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(box, param).Compile();
    }
}
