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

        // STI subtypes are declared via [SubTypeInfo] on their parent type.
        var stiSubTypeClrTypes = typeList
            .Where(t => t.GetCustomAttribute<TableInfo>() != null)
            .SelectMany(t => t.GetCustomAttributes<SubTypeInfo>())
            .Select(a => a.EntityType)
            .ToHashSet();

        // Joined-subtype types: have [TableInfo] declared on themselves AND their direct base type
        // also has [TableInfo] — so the entity's data is split across two tables joined by PK.
        // STI subtypes (already in stiSubTypeClrTypes) are excluded — they inherit [TableInfo]
        // from their root but are not joined subtypes.
        var joinedSubtypeClrTypes = typeList
            .Where(t => t.GetCustomAttribute<TableInfo>() != null
                     && !stiSubTypeClrTypes.Contains(t)
                     && t.BaseType?.GetCustomAttribute<TableInfo>() != null)
            .ToHashSet();

        // Pass 1: build root tables (not STI subtypes, not joined subtypes).
        var rootTables = typeList
            .Where(t => t.GetCustomAttribute<TableInfo>() != null
                     && !stiSubTypeClrTypes.Contains(t)
                     && !joinedSubtypeClrTypes.Contains(t))
            .Select(BuildTable)
            .ToList();

        var rootTableByType = rootTables.ToDictionary(t => t.EntityType);

        // Pass 2: build joined-subtype tables, wiring BaseTable to the root table.
        var joinedSubtypeTables = joinedSubtypeClrTypes
            .Select(t => BuildJoinedSubtypeTable(t, rootTableByType))
            .ToList();

        return new Database(rootTables.Concat(joinedSubtypeTables).ToList());
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
            .Select(p => BuildColumn(p))
            .ToList();
    }

    private static Column BuildColumn(PropertyInfo prop, bool overrideAutoIncrement = false)
    {
        var attr = prop.GetCustomAttribute<ColumnInfo>()!;
        return new Column(
            prop,
            attr.ColumnName ?? prop.Name,
            attr.ColumnType == ColumnType.Unknown ? null : attr.ColumnType,
            attr.Key,
            attr.KeyOrder,
            overrideAutoIncrement ? false : attr.AutoIncrement,
            attr.Nullable,
            attr.Unique,
            attr.IsDiscriminator,
            attr.IsVersion,
            attr.Size < 0 ? null : attr.Size,
            attr.Precision < 0 ? null : attr.Precision,
            attr.Scale < 0 ? null : attr.Scale,
            attr.DefaultValue);
    }

    /// <summary>
    /// Builds the subtype-side <see cref="Table"/> for a joined-subtype entity — one whose
    /// direct base also carries <c>[TableInfo]</c> and therefore owns a separate base table.
    /// Only the PK (explicitly assigned, not DB-generated) and columns declared directly on
    /// this subtype are included; inherited non-PK columns live in the base table.
    /// </summary>
    private static Table BuildJoinedSubtypeTable(Type type, Dictionary<Type, Table> rootTableByType)
    {
        var tableAttr = type.GetCustomAttribute<TableInfo>()!;
        var baseTable = rootTableByType[type.BaseType!];

        // Include: PK from anywhere in hierarchy (AutoIncrement overridden to false —
        // the join table's Id is explicitly assigned from the base INSERT, not DB-generated)
        // plus columns declared directly on this subtype.
        var columns = type.GetProperties()
            .Where(p => p.GetCustomAttribute<ColumnInfo>() != null)
            .Where(p => p.DeclaringType == type || p.GetCustomAttribute<ColumnInfo>()!.Key)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<ColumnInfo>()!;
                return attr.Key ? BuildColumn(p, overrideAutoIncrement: true) : BuildColumn(p);
            })
            .ToList();

        var indexes = type.GetCustomAttributes<IndexInfo>()
            .Select(a => new Index(a.Name, a.Columns, a.Unique))
            .ToList();

        return new Table(
            tableAttr.TableName ?? type.Name,
            tableAttr.Schema,
            type,
            columns,
            null,
            Array.Empty<SubType>(),
            BuildRelationships(type),
            indexes,
            baseTable);
    }

    private static List<SubType> BuildSubTypes(Type baseType, IReadOnlyList<Column> baseColumns)
    {
        var basePropertyNames = baseColumns.Select(c => c.Property.Name).ToHashSet();

        return baseType.GetCustomAttributes<SubTypeInfo>()
            .Select(a =>
            {
                var extraColumns = a.EntityType.GetProperties()
                    .Where(p => p.GetCustomAttribute<ColumnInfo>() != null && !basePropertyNames.Contains(p.Name))
                    .Select(p => BuildColumn(p))
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
                    o2o.Name ?? prop.Name, o2o.RelatedType, prop,
                    o2o.FromKeys, o2o.ToKeys, o2o.MappedBy, o2o.Nullable,
                    ToNullable(o2o.OnDelete), ToNullable(o2o.OnUpdate)));
                continue;
            }

            var o2m = prop.GetCustomAttribute<OneToManyRelationshipInfo>();
            if (o2m != null)
            {
                result.Add(new OneToManyRelationship(o2m.Name ?? prop.Name, o2m.RelatedType, prop, o2m.MappedBy));
                continue;
            }

            var m2o = prop.GetCustomAttribute<ManyToOneRelationshipInfo>();
            if (m2o != null)
            {
                result.Add(new ManyToOneRelationship(
                    m2o.Name ?? prop.Name, m2o.RelatedType, prop,
                    m2o.FromKeys, m2o.ToKeys, m2o.Nullable,
                    ToNullable(m2o.OnDelete), ToNullable(m2o.OnUpdate)));
                continue;
            }

            var m2m = prop.GetCustomAttribute<ManyToManyRelationshipInfo>();
            if (m2m != null)
            {
                result.Add(new ManyToManyRelationship(
                    m2m.Name ?? prop.Name, m2m.RelatedType, prop,
                    m2m.JoinTableName, m2m.JoinTableSchema,
                    m2m.LeftKeyColumns, m2m.RightKeyColumns,
                    m2m.LeftForeignKeys, m2m.RightForeignKeys,
                    m2m.MappedBy, ToNullable(m2m.OnDelete), ToNullable(m2m.OnUpdate)));
            }
        }

        return result;
    }
}
