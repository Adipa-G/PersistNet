using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PersistNet.DbInfo;

internal static class DbInfoExtractor
{
    public static Database Extract(Assembly assembly)
    {
        return Extract(assembly.GetTypes());
    }

    public static Database Extract(IEnumerable<Type> types)
    {
        var typeList = types.ToList();

        var subTypeClrTypes = typeList
            .Where(t => t.GetCustomAttribute<TableInfo>() != null)
            .SelectMany(t => t.GetCustomAttributes<SubTypeInfo>())
            .Select(a => a.EntityType)
            .ToHashSet();

        var tables = typeList
            .Where(t => t.GetCustomAttribute<TableInfo>() != null && !subTypeClrTypes.Contains(t))
            .Select(BuildTable)
            .ToList();

        return new Database(tables);
    }

    private static Table BuildTable(Type type)
    {
        var tableAttr = type.GetCustomAttribute<TableInfo>()!;
        var columns = BuildColumns(type);
        var discriminatorColumn = columns.FirstOrDefault(c => c.IsDiscriminator);
        var subTypes = BuildSubTypes(type, columns);
        var relationships = BuildRelationships(type);
        var indexes = type.GetCustomAttributes<IndexInfo>()
            .Select(a => new Index(a.Name, a.Columns, a.Unique))
            .ToList();

        return new Table(
            tableAttr.TableName ?? type.Name,
            tableAttr.Schema,
            type,
            columns,
            discriminatorColumn,
            subTypes,
            relationships,
            indexes);
    }

    private static ReferentialRuleType? ToNullable(ReferentialRuleType value) =>
        value == ReferentialRuleType.Unspecified ? null : value;

    private static List<Column> BuildColumns(Type type)
    {
        return type.GetProperties()
            .Where(p => p.GetCustomAttribute<ColumnInfo>() != null)
            .Select(BuildColumn)
            .ToList();
    }

    private static Column BuildColumn(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<ColumnInfo>()!;
        return new Column(
            prop,
            attr.ColumnName ?? prop.Name,
            attr.ColumnType == ColumnType.Unknown ? null : attr.ColumnType,
            attr.Key,
            attr.KeyOrder,
            attr.AutoIncrement,
            attr.Nullable,
            attr.Unique,
            attr.IsDiscriminator,
            attr.Size < 0 ? null : attr.Size,
            attr.Precision < 0 ? null : attr.Precision,
            attr.Scale < 0 ? null : attr.Scale,
            attr.DefaultValue);
    }

    private static List<SubType> BuildSubTypes(Type baseType, IReadOnlyList<Column> baseColumns)
    {
        var basePropertyNames = baseColumns.Select(c => c.Property.Name).ToHashSet();

        return baseType.GetCustomAttributes<SubTypeInfo>()
            .Select(a =>
            {
                var extraColumns = a.EntityType.GetProperties()
                    .Where(p => p.GetCustomAttribute<ColumnInfo>() != null && !basePropertyNames.Contains(p.Name))
                    .Select(BuildColumn)
                    .ToList();
                return new SubType(a.EntityType, a.DiscriminatorValue, extraColumns);
            })
            .ToList();
    }

    private static List<Relationship> BuildRelationships(Type type)
    {
        var result = new List<Relationship>();

        foreach (var prop in type.GetProperties())
        {
            var o2o = prop.GetCustomAttribute<OneToOneRelationshipInfo>();
            if (o2o != null)
            {
                result.Add(new OneToOneRelationship(
                    o2o.Name, o2o.RelatedType, prop,
                    o2o.FromKeys, o2o.ToKeys, o2o.MappedBy, o2o.Nullable,
                    ToNullable(o2o.OnDelete), ToNullable(o2o.OnUpdate)));
                continue;
            }

            var o2m = prop.GetCustomAttribute<OneToManyRelationshipInfo>();
            if (o2m != null)
            {
                result.Add(new OneToManyRelationship(o2m.Name, o2m.RelatedType, prop, o2m.MappedBy));
                continue;
            }

            var m2o = prop.GetCustomAttribute<ManyToOneRelationshipInfo>();
            if (m2o != null)
            {
                result.Add(new ManyToOneRelationship(
                    m2o.Name, m2o.RelatedType, prop,
                    m2o.FromKeys, m2o.ToKeys, m2o.Nullable,
                    ToNullable(m2o.OnDelete), ToNullable(m2o.OnUpdate)));
                continue;
            }

            var m2m = prop.GetCustomAttribute<ManyToManyRelationshipInfo>();
            if (m2m != null)
            {
                result.Add(new ManyToManyRelationship(
                    m2m.Name, m2m.RelatedType, prop,
                    m2m.JoinTableName, m2m.JoinTableSchema,
                    m2m.LeftKeyColumns, m2m.RightKeyColumns,
                    m2m.LeftForeignKeys, m2m.RightForeignKeys,
                    m2m.MappedBy, ToNullable(m2m.OnDelete), ToNullable(m2m.OnUpdate)));
            }
        }

        return result;
    }
}
