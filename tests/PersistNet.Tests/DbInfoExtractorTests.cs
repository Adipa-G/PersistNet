using System.Collections.Generic;
using PersistNet.DbInfo;
using Xunit;

namespace PersistNet.Tests;

public class DbInfoExtractorTests
{
    #region Fixture types

    private class NotATable
    {
        [ColumnInfo] public int Id { get; set; }
    }

    [TableInfo(TableName = "animals", Schema = "zoo")]
    [SubTypeInfo(typeof(Dog), "dog")]
    [SubTypeInfo(typeof(Cat), "cat")]
    [IndexInfo(Name = "idx_name", Columns = new[] { "animal_name" })]
    [IndexInfo(Name = "idx_species_age", Columns = new[] { "Species", "Age" }, Unique = true)]
    private class Animal
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo(ColumnName = "animal_name", Nullable = false)]
        public string Name { get; set; } = "";

        [ColumnInfo(IsDiscriminator = true)]
        public string Species { get; set; } = "";

        [ColumnInfo(Nullable = true)]
        public int? Age { get; set; }
    }

    private class Dog : Animal
    {
        [ColumnInfo]
        public string Breed { get; set; } = "";
    }

    private class Cat : Animal
    {
        [ColumnInfo]
        public bool IsIndoor { get; set; }
    }

    [TableInfo]
    private class SimpleEntity
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }
    }

    [TableInfo(TableName = "orders")]
    private class Order
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [OneToOneRelationshipInfo(Name = "order_receipt", RelatedType = typeof(Receipt),
            FromKeys = new[] { "ReceiptId" }, ToKeys = new[] { "Id" })]
        public Receipt? Receipt { get; set; }

        [ManyToOneRelationshipInfo(Name = "order_customer", RelatedType = typeof(Customer),
            FromKeys = new[] { "CustomerId" }, ToKeys = new[] { "Id" }, Nullable = true)]
        public Customer? Customer { get; set; }

        [OneToManyRelationshipInfo(Name = "order_items", RelatedType = typeof(OrderItem),
            MappedBy = "Order")]
        public List<OrderItem>? Items { get; set; }

        [ManyToManyRelationshipInfo(Name = "order_tags", RelatedType = typeof(Tag),
            JoinTableName = "order_tags", JoinTableSchema = "dbo",
            LeftKeyColumns = new[] { "OrderId" }, RightKeyColumns = new[] { "TagId" },
            LeftForeignKeys = new[] { "Id" }, RightForeignKeys = new[] { "Id" },
            MappedBy = "Orders")]
        public List<Tag>? Tags { get; set; }
    }

    private class Receipt { }
    private class Customer { }
    private class OrderItem { }
    private class Tag { }

    #endregion

    [Fact]
    public void GivenTypeWithoutTableInfo_WhenExtractCalled_ThenItIsFiltered()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(NotATable), typeof(SimpleEntity) });

        Assert.Single(db.Tables);
        Assert.Equal(typeof(SimpleEntity), db.Tables[0].EntityType);
    }

    [Fact]
    public void GivenTableInfoWithExplicitName_WhenExtractCalled_ThenNameAndSchemaMatchAttribute()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal) });

        Assert.Equal("animals", db.Tables[0].Name);
        Assert.Equal("zoo", db.Tables[0].Schema);
    }

    [Fact]
    public void GivenTableInfoWithoutName_WhenExtractCalled_ThenNameDefaultsToTypeName()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(SimpleEntity) });

        Assert.Equal("SimpleEntity", db.Tables[0].Name);
        Assert.Null(db.Tables[0].Schema);
    }

    [Fact]
    public void GivenColumnInfoAttributes_WhenExtractCalled_ThenColumnsAreMappedCorrectly()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal) });
        var table = db.Tables[0];

        var id = Assert.Single(table.Columns, c => c.Property.Name == "Id");
        Assert.Equal("Id", id.ColumnName);
        Assert.True(id.IsKey);
        Assert.True(id.AutoIncrement);

        var name = Assert.Single(table.Columns, c => c.Property.Name == "Name");
        Assert.Equal("animal_name", name.ColumnName);
        Assert.False(name.Nullable);

        var age = Assert.Single(table.Columns, c => c.Property.Name == "Age");
        Assert.True(age.Nullable);
    }

    [Fact]
    public void GivenColumnWithIsDiscriminatorTrue_WhenExtractCalled_ThenDiscriminatorColumnIsSet()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal) });
        var table = db.Tables[0];

        Assert.NotNull(table.DiscriminatorColumn);
        Assert.Equal("Species", table.DiscriminatorColumn!.Property.Name);
        Assert.True(table.DiscriminatorColumn.IsDiscriminator);
    }

    [Fact]
    public void GivenNoDiscriminatorColumn_WhenExtractCalled_ThenDiscriminatorColumnIsNull()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(SimpleEntity) });

        Assert.Null(db.Tables[0].DiscriminatorColumn);
    }

    [Fact]
    public void GivenSubTypeInfoAttributes_WhenExtractCalled_ThenSubTypesArePopulatedWithExtraColumns()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal) });
        var table = db.Tables[0];

        Assert.Equal(2, table.SubTypes.Count);

        var dog = Assert.Single(table.SubTypes, s => s.EntityType == typeof(Dog));
        Assert.Equal("dog", dog.DiscriminatorValue);
        var dogBreed = Assert.Single(dog.ExtraColumns);
        Assert.Equal("Breed", dogBreed.Property.Name);

        var cat = Assert.Single(table.SubTypes, s => s.EntityType == typeof(Cat));
        Assert.Equal("cat", cat.DiscriminatorValue);
        var catIndoor = Assert.Single(cat.ExtraColumns);
        Assert.Equal("IsIndoor", catIndoor.Property.Name);
    }

    [Fact]
    public void GivenSubTypeClrTypesPassedIn_WhenExtractCalled_ThenTheyAreNotTopLevelTables()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal), typeof(Dog), typeof(Cat) });

        Assert.Single(db.Tables);
        Assert.Equal(typeof(Animal), db.Tables[0].EntityType);
    }

    [Fact]
    public void GivenMultipleIndexInfoAttributes_WhenExtractCalled_ThenAllIndexesArePopulated()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Animal) });
        var table = db.Tables[0];

        Assert.Equal(2, table.Indexes.Count);

        var idx = Assert.Single(table.Indexes, i => i.Name == "idx_name");
        Assert.Equal(new[] { "animal_name" }, idx.Columns);
        Assert.False(idx.Unique);

        var idxUnique = Assert.Single(table.Indexes, i => i.Name == "idx_species_age");
        Assert.Equal(new[] { "Species", "Age" }, idxUnique.Columns);
        Assert.True(idxUnique.Unique);
    }

    [Fact]
    public void GivenOneToOneRelationshipInfo_WhenExtractCalled_ThenRelationshipIsMappedCorrectly()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Order) });
        var rel = Assert.Single(db.Tables[0].Relationships, r => r is OneToOneRelationship);

        var o2o = Assert.IsType<OneToOneRelationship>(rel);
        Assert.Equal("order_receipt", o2o.Name);
        Assert.Equal(typeof(Receipt), o2o.RelatedType);
        Assert.Equal(new[] { "ReceiptId" }, o2o.FromKeys);
        Assert.Equal(new[] { "Id" }, o2o.ToKeys);
        Assert.Null(o2o.OnDelete);
    }

    [Fact]
    public void GivenManyToOneRelationshipInfo_WhenExtractCalled_ThenRelationshipIsMappedCorrectly()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Order) });
        var rel = Assert.Single(db.Tables[0].Relationships, r => r is ManyToOneRelationship);

        var m2o = Assert.IsType<ManyToOneRelationship>(rel);
        Assert.Equal("order_customer", m2o.Name);
        Assert.Equal(typeof(Customer), m2o.RelatedType);
        Assert.True(m2o.Nullable);
        Assert.Null(m2o.OnUpdate);
    }

    [Fact]
    public void GivenOneToManyRelationshipInfo_WhenExtractCalled_ThenRelationshipIsMappedCorrectly()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Order) });
        var rel = Assert.Single(db.Tables[0].Relationships, r => r is OneToManyRelationship);

        var o2m = Assert.IsType<OneToManyRelationship>(rel);
        Assert.Equal("order_items", o2m.Name);
        Assert.Equal(typeof(OrderItem), o2m.RelatedType);
        Assert.Equal("Order", o2m.MappedBy);
    }

    [Fact]
    public void GivenManyToManyRelationshipInfo_WhenExtractCalled_ThenRelationshipIsMappedCorrectly()
    {
        var db = DbInfoExtractor.Extract(new[] { typeof(Order) });
        var rel = Assert.Single(db.Tables[0].Relationships, r => r is ManyToManyRelationship);

        var m2m = Assert.IsType<ManyToManyRelationship>(rel);
        Assert.Equal("order_tags", m2m.Name);
        Assert.Equal(typeof(Tag), m2m.RelatedType);
        Assert.Equal("order_tags", m2m.JoinTableName);
        Assert.Equal("dbo", m2m.JoinTableSchema);
        Assert.Equal(new[] { "OrderId" }, m2m.LeftKeyColumns);
        Assert.Equal(new[] { "TagId" }, m2m.RightKeyColumns);
        Assert.Equal(new[] { "Id" }, m2m.LeftForeignKeys);
        Assert.Equal(new[] { "Id" }, m2m.RightForeignKeys);
        Assert.Equal("Orders", m2m.MappedBy);
        Assert.Null(m2m.OnDelete);
    }

    [Fact]
    public void GivenAssemblyWithAnnotatedTypes_WhenExtractCalled_ThenTablesAreFound()
    {
        var db = DbInfoExtractor.Extract(typeof(DbInfoExtractorTests).Assembly);

        Assert.Contains(db.Tables, t => t.EntityType == typeof(Animal));
        Assert.Contains(db.Tables, t => t.EntityType == typeof(Order));
        Assert.Contains(db.Tables, t => t.EntityType == typeof(SimpleEntity));
        Assert.DoesNotContain(db.Tables, t => t.EntityType == typeof(Dog));
        Assert.DoesNotContain(db.Tables, t => t.EntityType == typeof(Cat));
    }
}
