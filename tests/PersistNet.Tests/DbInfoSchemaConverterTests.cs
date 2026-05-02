using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;
using PersistNet.Schema;
using Xunit;

namespace PersistNet.Tests;

public class DbInfoSchemaConverterTests
{
    #region Fixture types

    [TableInfo(TableName = "products", Schema = "store")]
    private class ProductRow
    {
        [ColumnInfo(Key = true, AutoIncrement = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnName = "prod_name", ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
        public string Name { get; set; } = "";

        [ColumnInfo(ColumnType = ColumnType.Decimal, Precision = 8, Scale = 2, Nullable = true)]
        public decimal? Price { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Boolean, DefaultValue = "true")]
        public bool Active { get; set; }
    }

    [TableInfo]
    private class AllTypesRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Guid)]      public Guid     GuidCol     { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Long)]                   public long     LongCol     { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Boolean)]                public bool     BoolCol     { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Char)]                   public char     CharCol     { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Integer)]                public int      IntCol      { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Date)]                   public DateTime DateCol     { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Double)]                 public double   DoubleCol   { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Float)]                  public float    FloatCol    { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Timestamp)]              public DateTime TsCol       { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Varchar)]                public string   VarcharCol  { get; set; } = "";
        [ColumnInfo(ColumnType = ColumnType.Decimal)]                public decimal  DecimalCol  { get; set; }
        [ColumnInfo(ColumnType = ColumnType.Version)]                public long     VersionCol  { get; set; }
        [ColumnInfo]                                                 public string   NullTypeCol { get; set; } = "";
    }

    [TableInfo]
    private class CompositePkRow
    {
        [ColumnInfo(Key = true, KeyOrder = 2, ColumnType = ColumnType.Varchar)]
        public string CountryCode { get; set; } = "";

        [ColumnInfo(Key = true, KeyOrder = 0, ColumnType = ColumnType.Integer)]
        public int CityId { get; set; }

        [ColumnInfo(Key = true, KeyOrder = 1, ColumnType = ColumnType.Integer)]
        public int RegionId { get; set; }
    }

    [TableInfo]
    private class NonKeyRow
    {
        [ColumnInfo(ColumnType = ColumnType.Varchar)]
        public string Name { get; set; } = "";
    }

    [TableInfo]
    [IndexInfo(Name = "idx_sku", Columns = new[] { "Sku" }, Unique = true)]
    [IndexInfo(Name = "idx_category_price", Columns = new[] { "Category", "Price" })]
    private class IndexedRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Varchar)]
        public string Sku { get; set; } = "";

        [ColumnInfo(ColumnType = ColumnType.Varchar)]
        public string Category { get; set; } = "";

        [ColumnInfo(ColumnType = ColumnType.Decimal)]
        public decimal Price { get; set; }
    }

    [TableInfo]
    [SubTypeInfo(typeof(ElectricCarRow), "electric")]
    private class CarRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(IsDiscriminator = true, ColumnType = ColumnType.Varchar, Nullable = false)]
        public string Type { get; set; } = "";
    }

    private class ElectricCarRow : CarRow
    {
        [ColumnInfo(ColumnType = ColumnType.Integer)]
        public int RangeKm { get; set; }
    }

    [TableInfo(TableName = "line_items")]
    private class LineItemRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Integer)]
        public int PartId { get; set; }

        [ManyToOneRelationshipInfo(
            Name = "lineitem_part",
            RelatedType = typeof(PartRow),
            FromKeys = new[] { "PartId" },
            ToKeys = new[] { "Id" },
            OnDelete = ReferentialRuleType.Cascade,
            OnUpdate = ReferentialRuleType.Restrict)]
        public PartRow? Part { get; set; }
    }

    [TableInfo(TableName = "parts", Schema = "inventory")]
    private class PartRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }
    }

    [TableInfo]
    private class ReplyRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Integer)]
        public int ForumId { get; set; }

        // ToKeys intentionally left empty — should fall back to ForumRow's PK
        [ManyToOneRelationshipInfo(RelatedType = typeof(ForumRow), FromKeys = new[] { "ForumId" })]
        public ForumRow? Forum { get; set; }
    }

    [TableInfo]
    private class ForumRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }
    }

    [TableInfo(TableName = "contracts")]
    private class ContractRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Integer)]
        public int SummaryId { get; set; }

        // Owning side
        [OneToOneRelationshipInfo(
            Name = "contract_summary",
            RelatedType = typeof(SummaryRow),
            FromKeys = new[] { "SummaryId" },
            ToKeys = new[] { "Id" })]
        public SummaryRow? Summary { get; set; }

        // One-to-many: FK lives on the ClauseRow side, not here
        [OneToManyRelationshipInfo(Name = "contract_clauses", RelatedType = typeof(ClauseRow), MappedBy = "Contract")]
        public List<ClauseRow>? Clauses { get; set; }
    }

    [TableInfo]
    private class SummaryRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        // Inverse side — must NOT generate a FK
        [OneToOneRelationshipInfo(RelatedType = typeof(ContractRow), MappedBy = "Summary")]
        public ContractRow? Contract { get; set; }
    }

    // Needed only as a RelatedType reference; not extracted as a standalone table
    private class ClauseRow { }

    [TableInfo(TableName = "students")]
    private class StudentRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        // Owning side
        [ManyToManyRelationshipInfo(
            RelatedType = typeof(CourseRow),
            JoinTableName = "enrollments",
            JoinTableSchema = "academic",
            LeftKeyColumns = new[] { "StudentId" },
            RightKeyColumns = new[] { "CourseId" },
            LeftForeignKeys = new[] { "Id" },
            RightForeignKeys = new[] { "Id" })]
        public List<CourseRow>? Courses { get; set; }
    }

    [TableInfo(TableName = "courses")]
    private class CourseRow
    {
        [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        // Inverse side — must NOT generate a join table
        [ManyToManyRelationshipInfo(RelatedType = typeof(StudentRow), MappedBy = "Courses")]
        public List<StudentRow>? Students { get; set; }
    }

    #endregion

    #region Helpers

    private static SchemaSnapshot Convert(params Type[] types)
    {
        var db = DbInfoExtractor.Extract(types);
        return DbInfoSchemaConverter.Convert(db);
    }

    private static SchemaTable GetTable(SchemaSnapshot snapshot, string name) =>
        snapshot.Tables.Single(t => t.Name == name);

    private static SchemaColumn GetColumn(SchemaTable table, string name) =>
        table.Columns.Single(c => c.Name == name);

    #endregion

    // ── Table mapping ──────────────────────────────────────────────────────

    [Fact]
    public void GivenExplicitTableNameAndSchema_WhenConvert_ThenSchemaTableHasCorrectNameAndSchema()
    {
        var snapshot = Convert(typeof(ProductRow));
        var table = GetTable(snapshot, "products");

        Assert.Equal("products", table.Name);
        Assert.Equal("store", table.Schema);
    }

    [Fact]
    public void GivenNoTableName_WhenConvert_ThenNameDefaultsToTypeName()
    {
        var snapshot = Convert(typeof(NonKeyRow));
        var table = GetTable(snapshot, "NonKeyRow");

        Assert.Equal("NonKeyRow", table.Name);
        Assert.Null(table.Schema);
    }

    // ── Column mapping ─────────────────────────────────────────────────────

    [Fact]
    public void GivenColumnAttributes_WhenConvert_ThenColumnPropertiesMapped()
    {
        var snapshot = Convert(typeof(ProductRow));
        var table = GetTable(snapshot, "products");

        var id = GetColumn(table, "Id");
        Assert.True(id.IsAutoIncrement);
        Assert.False(id.IsNullable);

        var name = GetColumn(table, "prod_name");
        Assert.Equal("VARCHAR", name.DbType);
        Assert.Equal(100, name.Size);
        Assert.False(name.IsNullable);

        var price = GetColumn(table, "Price");
        Assert.Equal("DECIMAL", price.DbType);
        Assert.Equal(8, price.Precision);
        Assert.Equal(2, price.Scale);
        Assert.True(price.IsNullable);

        var active = GetColumn(table, "Active");
        Assert.Equal("true", active.DefaultValue);
        Assert.False(active.IsNullable);
    }

    [Fact]
    public void GivenAllColumnTypes_WhenConvert_ThenDbTypesAreMappedToCanonicalStrings()
    {
        var snapshot = Convert(typeof(AllTypesRow));
        var table = GetTable(snapshot, "AllTypesRow");

        Assert.Equal("GUID",      GetColumn(table, "GuidCol").DbType);
        Assert.Equal("BIGINT",    GetColumn(table, "LongCol").DbType);
        Assert.Equal("BOOLEAN",   GetColumn(table, "BoolCol").DbType);
        Assert.Equal("CHAR",      GetColumn(table, "CharCol").DbType);
        Assert.Equal("INTEGER",   GetColumn(table, "IntCol").DbType);
        Assert.Equal("DATE",      GetColumn(table, "DateCol").DbType);
        Assert.Equal("DOUBLE",    GetColumn(table, "DoubleCol").DbType);
        Assert.Equal("FLOAT",     GetColumn(table, "FloatCol").DbType);
        Assert.Equal("TIMESTAMP", GetColumn(table, "TsCol").DbType);
        Assert.Equal("VARCHAR",   GetColumn(table, "VarcharCol").DbType);
        Assert.Equal("DECIMAL",   GetColumn(table, "DecimalCol").DbType);
        Assert.Equal("BIGINT",    GetColumn(table, "VersionCol").DbType);  // Version maps to BIGINT
        Assert.Equal("UNKNOWN",   GetColumn(table, "NullTypeCol").DbType); // null ColumnType
    }

    // ── Primary key ────────────────────────────────────────────────────────

    [Fact]
    public void GivenSingleKeyColumn_WhenConvert_ThenPrimaryKeyContainsThatColumn()
    {
        var snapshot = Convert(typeof(ProductRow));
        var pk = GetTable(snapshot, "products").PrimaryKey;

        Assert.NotNull(pk);
        Assert.Equal(new[] { "Id" }, pk!.Columns);
    }

    [Fact]
    public void GivenCompositeKey_WhenConvert_ThenPrimaryKeyColumnsOrderedByKeyOrder()
    {
        var snapshot = Convert(typeof(CompositePkRow));
        var pk = GetTable(snapshot, "CompositePkRow").PrimaryKey;

        Assert.NotNull(pk);
        // KeyOrder 0 → CityId, 1 → RegionId, 2 → CountryCode
        Assert.Equal(new[] { "CityId", "RegionId", "CountryCode" }, pk!.Columns);
    }

    [Fact]
    public void GivenNoKeyColumns_WhenConvert_ThenPrimaryKeyIsNull()
    {
        var snapshot = Convert(typeof(NonKeyRow));

        Assert.Null(GetTable(snapshot, "NonKeyRow").PrimaryKey);
    }

    // ── Indexes ────────────────────────────────────────────────────────────

    [Fact]
    public void GivenIndexInfoAttributes_WhenConvert_ThenIndexesAreMapped()
    {
        var snapshot = Convert(typeof(IndexedRow));
        var table = GetTable(snapshot, "IndexedRow");

        Assert.Equal(2, table.Indexes.Count);

        var sku = Assert.Single(table.Indexes, i => i.Name == "idx_sku");
        Assert.Equal(new[] { "Sku" }, sku.Columns);
        Assert.True(sku.IsUnique);

        var catPrice = Assert.Single(table.Indexes, i => i.Name == "idx_category_price");
        Assert.Equal(new[] { "Category", "Price" }, catPrice.Columns);
        Assert.False(catPrice.IsUnique);
    }

    // ── SubType (single-table inheritance) ─────────────────────────────────

    [Fact]
    public void GivenSubType_WhenConvert_ThenExtraColumnsAreMergedAndForcedNullable()
    {
        var snapshot = Convert(typeof(CarRow));
        var table = GetTable(snapshot, "CarRow");

        // Base columns retain their declared nullability
        Assert.False(GetColumn(table, "Id").IsNullable);
        Assert.False(GetColumn(table, "Type").IsNullable);

        // Subtype extra column is forced nullable (not all rows have a value)
        var rangeKm = GetColumn(table, "RangeKm");
        Assert.True(rangeKm.IsNullable);
        Assert.Equal("INTEGER", rangeKm.DbType);
    }

    // ── Foreign keys ───────────────────────────────────────────────────────

    [Fact]
    public void GivenManyToOneRelationship_WhenConvert_ThenForeignKeyCreatedWithCorrectDetails()
    {
        var snapshot = Convert(typeof(LineItemRow), typeof(PartRow));
        var table = GetTable(snapshot, "line_items");

        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal("lineitem_part", fk.Name);
        Assert.Equal(new[] { "PartId" }, fk.FromColumns);
        Assert.Equal("parts", fk.ToTable);
        Assert.Equal("inventory", fk.ToSchema);
        Assert.Equal(new[] { "Id" }, fk.ToColumns);
        Assert.Equal(ReferentialRuleType.Cascade, fk.OnDelete);
        Assert.Equal(ReferentialRuleType.Restrict, fk.OnUpdate);
    }

    [Fact]
    public void GivenManyToOneWithEmptyToKeys_WhenConvert_ThenForeignKeyToColumnsDefaultsToRelatedTablePrimaryKey()
    {
        var snapshot = Convert(typeof(ReplyRow), typeof(ForumRow));
        var table = GetTable(snapshot, "ReplyRow");

        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal(new[] { "ForumId" }, fk.FromColumns);
        Assert.Equal(new[] { "Id" }, fk.ToColumns); // resolved from ForumRow's PK
    }

    [Fact]
    public void GivenOneToOneOwningRelationship_WhenConvert_ThenForeignKeyIsCreated()
    {
        var snapshot = Convert(typeof(ContractRow), typeof(SummaryRow));
        var table = GetTable(snapshot, "contracts");

        var fk = Assert.Single(table.ForeignKeys, f => f.Name == "contract_summary");
        Assert.Equal(new[] { "SummaryId" }, fk.FromColumns);
        Assert.Equal("SummaryRow", fk.ToTable);
        Assert.Equal(new[] { "Id" }, fk.ToColumns);
    }

    [Fact]
    public void GivenOneToOneInverseRelationship_WhenConvert_ThenNoForeignKeyOnInverseTable()
    {
        var snapshot = Convert(typeof(ContractRow), typeof(SummaryRow));

        Assert.Empty(GetTable(snapshot, "SummaryRow").ForeignKeys);
    }

    [Fact]
    public void GivenOneToManyRelationship_WhenConvert_ThenNoForeignKeyOnOwnerTable()
    {
        var snapshot = Convert(typeof(ContractRow), typeof(SummaryRow));
        var contractTable = GetTable(snapshot, "contracts");

        // Only the O2O FK to SummaryRow should be present; the O2M adds no FK here
        Assert.Single(contractTable.ForeignKeys);
        Assert.Equal("contract_summary", contractTable.ForeignKeys[0].Name);
    }

    // ── Many-to-many ───────────────────────────────────────────────────────

    [Fact]
    public void GivenManyToManyOwningRelationship_WhenConvert_ThenJoinTableCreatedWithCorrectStructure()
    {
        var snapshot = Convert(typeof(StudentRow), typeof(CourseRow));

        // 2 entity tables + 1 join table
        Assert.Equal(3, snapshot.Tables.Count);

        var join = GetTable(snapshot, "enrollments");
        Assert.Equal("academic", join.Schema);

        // Join table columns carry types copied from the source entity PKs
        Assert.Equal("INTEGER", GetColumn(join, "StudentId").DbType);
        Assert.False(GetColumn(join, "StudentId").IsNullable);
        Assert.Equal("INTEGER", GetColumn(join, "CourseId").DbType);

        // PK spans both FK column sets
        Assert.NotNull(join.PrimaryKey);
        Assert.Equal(new[] { "StudentId", "CourseId" }, join.PrimaryKey!.Columns);

        // Two FKs: one pointing to each entity table
        Assert.Equal(2, join.ForeignKeys.Count);
        Assert.Single(join.ForeignKeys, f => f.ToTable == "students");
        Assert.Single(join.ForeignKeys, f => f.ToTable == "courses");
    }

    [Fact]
    public void GivenManyToManyInverseRelationship_WhenConvert_ThenNoExtraJoinTableCreated()
    {
        var snapshot = Convert(typeof(StudentRow), typeof(CourseRow));

        // CourseRow (inverse side) must not produce a second join table
        Assert.Equal(3, snapshot.Tables.Count); // students + courses + one "enrollments"
        Assert.Single(snapshot.Tables, t => t.Name == "enrollments");
    }

    // ── Multi-table ────────────────────────────────────────────────────────

    [Fact]
    public void GivenMultipleTables_WhenConvert_ThenAllTablesIncludedInSnapshot()
    {
        var snapshot = Convert(typeof(ProductRow), typeof(IndexedRow), typeof(CarRow));

        Assert.Equal(3, snapshot.Tables.Count);
        Assert.Contains(snapshot.Tables, t => t.Name == "products");
        Assert.Contains(snapshot.Tables, t => t.Name == "IndexedRow");
        Assert.Contains(snapshot.Tables, t => t.Name == "CarRow");
    }
}
