using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbInfo;
using PersistNet.Entities;
using PersistNet.Entities.VirtualDb;
using Xunit;

namespace PersistNet.Tests;

public class ChangeSetBuilderTests
{
    // -------------------------------------------------------------------------
    // Fixture types
    // -------------------------------------------------------------------------

    [TableInfo(TableName = "customers")]
    private class Customer
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [OneToManyRelationshipInfo(RelatedType = typeof(Order), MappedBy = "Customer")]
        public List<Order>? Orders { get; set; }
    }

    [TableInfo(TableName = "orders")]
    private class Order
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Description { get; set; } = "";

        [ColumnInfo]
        public int CustomerId { get; set; }

        [ManyToOneRelationshipInfo(RelatedType = typeof(Customer),
            FromKeys = new[] { "CustomerId" }, ToKeys = new[] { "Id" })]
        public Customer? Customer { get; set; }
    }

    [TableInfo(TableName = "guid_entities")]
    private class GuidEntity
    {
        [ColumnInfo(Key = true)]
        public Guid Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";
    }

    [TableInfo(TableName = "vehicles")]
    [SubTypeInfo(typeof(Car), "car")]
    [SubTypeInfo(typeof(Truck), "truck")]
    private class Vehicle
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo(IsDiscriminator = true)]
        public string VehicleType { get; set; } = "";

        [ColumnInfo]
        public string Make { get; set; } = "";
    }

    private class Car : Vehicle
    {
        [ColumnInfo]
        public int NumDoors { get; set; }
    }

    private class Truck : Vehicle
    {
        [ColumnInfo]
        public double Payload { get; set; }
    }

    [TableInfo(TableName = "students")]
    private class Student
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ManyToManyRelationshipInfo(
            RelatedType = typeof(Course),
            JoinTableName = "student_course",
            LeftKeyColumns = new[] { "StudentId" },
            RightKeyColumns = new[] { "CourseId" },
            LeftForeignKeys = new[] { "Id" },
            RightForeignKeys = new[] { "Id" })]
        public List<Course>? Courses { get; set; }
    }

    [TableInfo(TableName = "courses")]
    private class Course
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Title { get; set; } = "";

        [ManyToManyRelationshipInfo(RelatedType = typeof(Student), MappedBy = "Courses")]
        public List<Student>? Students { get; set; }
    }

    // -------------------------------------------------------------------------
    // DbInfoCache
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_SameAssembly_When_GetOrExtractCalledTwice_Then_ReturnsSameInstance()
    {
        var db1 = DbInfoCache.GetOrExtract(typeof(Customer).Assembly);
        var db2 = DbInfoCache.GetOrExtract(typeof(Customer).Assembly);

        Assert.Same(db1, db2);
    }

    // -------------------------------------------------------------------------
    // IsInsert (tested indirectly via Save OperationType)
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_IntPkIsZero_When_Save_Then_ProducesInsert()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Customer { Id = 0, Name = "Alice" });

        Assert.Equal(OperationType.Insert, builder.PendingOperations.Single().Type);
    }

    [Fact]
    public void Given_IntPkIsSet_When_Save_Then_ProducesUpdate()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Customer { Id = 5, Name = "Alice" });

        Assert.Equal(OperationType.Update, builder.PendingOperations.Single().Type);
    }

    [Fact]
    public void Given_GuidPkIsEmpty_When_Save_Then_ProducesInsert()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new GuidEntity { Id = Guid.Empty, Name = "X" });

        Assert.Equal(OperationType.Insert, builder.PendingOperations.Single().Type);
    }

    [Fact]
    public void Given_GuidPkIsSet_When_Save_Then_ProducesUpdate()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new GuidEntity { Id = Guid.NewGuid(), Name = "X" });

        Assert.Equal(OperationType.Update, builder.PendingOperations.Single().Type);
    }

    // -------------------------------------------------------------------------
    // MapToRow — column names and values
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_InsertOperation_When_Save_Then_EmitsAllColumnsWithCorrectNamesAndValues()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Customer { Id = 0, Name = "Bob" });

        var row = builder.PendingOperations.Single().Row;
        Assert.Equal(2, row.Cells.Count); // Id + Name
        Assert.Contains(row.Cells, c => c.ColumnName == "Id" && Equals(c.Value, 0));
        Assert.Contains(row.Cells, c => c.ColumnName == "Name" && Equals(c.Value, "Bob"));
    }

    [Fact]
    public void Given_DeleteOperation_When_Delete_Then_EmitsKeyColumnsOnly()
    {
        var builder = new ChangeSetBuilder();
        builder.Delete(new Customer { Id = 7, Name = "Bob" });

        var row = builder.PendingOperations.Single().Row;
        Assert.Single(row.Cells);
        Assert.Equal("Id", row.Cells[0].ColumnName);
        Assert.Equal(7, row.Cells[0].Value);
    }

    // -------------------------------------------------------------------------
    // STI — discriminator + extra columns
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_SubtypeEntity_When_Save_Then_EmitsDiscriminatorFromMetadataAndExtraColumns()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Car { Id = 0, Make = "Toyota", NumDoors = 4 });

        var row = builder.PendingOperations.Single().Row;

        // Discriminator must use metadata value "car", not whatever the property says.
        var discriminator = row.Cells.Single(c => c.ColumnName == "VehicleType");
        Assert.Equal("car", discriminator.Value);

        // Extra subtype column.
        Assert.Contains(row.Cells, c => c.ColumnName == "NumDoors" && Equals(c.Value, 4));
    }

    [Fact]
    public void Given_SubtypeEntity_When_Save_Then_DoesNotDuplicateDiscriminatorCell()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Truck { Id = 0, Make = "Ford", Payload = 5.0 });

        var row = builder.PendingOperations.Single().Row;
        Assert.Single(row.Cells.Where(c => c.ColumnName == "VehicleType"));
    }

    // -------------------------------------------------------------------------
    // Cascade — parent + child in one call
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_ParentWithChildren_When_Save_Then_AddsBothToChangeSet()
    {
        var customer = new Customer
        {
            Id = 0,
            Name = "Alice",
            Orders = new List<Order>
            {
                new Order { Id = 0, Description = "Order1", Customer = null }
            }
        };

        var builder = new ChangeSetBuilder();
        builder.Save(customer);

        Assert.Equal(2, builder.PendingOperations.Count);
        Assert.Contains(builder.PendingOperations, o => o.TableName == "customers");
        Assert.Contains(builder.PendingOperations, o => o.TableName == "orders");
    }

    [Fact]
    public void Given_ChildWithParent_When_Save_Then_BothAddedToChangeSet()
    {
        var customer = new Customer { Id = 0, Name = "Alice" };
        var order = new Order { Id = 0, Description = "O1", Customer = customer };

        var builder = new ChangeSetBuilder();
        builder.Save(order);

        Assert.Equal(2, builder.PendingOperations.Count);
        Assert.Contains(builder.PendingOperations, o => o.TableName == "customers");
        Assert.Contains(builder.PendingOperations, o => o.TableName == "orders");
    }

    // -------------------------------------------------------------------------
    // Deduplication
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_SameInstanceReferencedByTwoChildren_When_Save_Then_OnlyOneOperationForParent()
    {
        var customer = new Customer { Id = 0, Name = "Alice" };
        var order1 = new Order { Id = 0, Description = "O1", Customer = customer };
        var order2 = new Order { Id = 0, Description = "O2", Customer = customer };
        customer.Orders = new List<Order> { order1, order2 };

        var builder = new ChangeSetBuilder();
        builder.Save(customer);

        var customerOps = builder.PendingOperations.Where(o => o.TableName == "customers").ToList();
        Assert.Single(customerOps);
    }

    // -------------------------------------------------------------------------
    // GetOrderedBatches — insert order (parent before child)
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_SaveGraph_When_GetOrderedBatches_Then_ParentTableBeforeChildTable()
    {
        var customer = new Customer { Id = 0, Name = "Alice" };
        var order = new Order { Id = 0, Description = "O1", Customer = customer };
        customer.Orders = new List<Order> { order };

        var builder = new ChangeSetBuilder();
        builder.Save(customer);

        var batches = builder.GetOrderedBatches();
        var insertBatches = batches.Where(b => b.OperationType == OperationType.Insert).ToList();

        var customerIdx = insertBatches.FindIndex(b => b.TableName == "customers");
        var orderIdx = insertBatches.FindIndex(b => b.TableName == "orders");

        Assert.True(customerIdx < orderIdx, "customers batch must appear before orders batch");
    }

    // -------------------------------------------------------------------------
    // GetOrderedBatches — delete order (child before parent)
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_DeleteGraph_When_GetOrderedBatches_Then_ChildTableBeforeParentTable()
    {
        var customer = new Customer { Id = 3, Name = "Alice" };
        var order = new Order { Id = 7, Description = "O1", Customer = customer };
        customer.Orders = new List<Order> { order };

        var builder = new ChangeSetBuilder();
        builder.Delete(customer);

        var batches = builder.GetOrderedBatches();
        var deleteBatches = batches.Where(b => b.OperationType == OperationType.Delete).ToList();

        var customerIdx = deleteBatches.FindIndex(b => b.TableName == "customers");
        var orderIdx = deleteBatches.FindIndex(b => b.TableName == "orders");

        Assert.True(orderIdx < customerIdx, "orders batch must appear before customers batch");
    }

    // -------------------------------------------------------------------------
    // GetOrderedBatches — M2M join table ordering
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_M2MSave_When_GetOrderedBatches_Then_JoinTableBatchAfterBothEntityBatches()
    {
        // Both entities have default PKs → all three operations are INSERTs.
        var course = new Course { Id = 0, Title = "Maths" };
        var student = new Student
        {
            Id = 0,
            Name = "Alice",
            Courses = new List<Course> { course }
        };

        var builder = new ChangeSetBuilder();
        builder.Save(student);

        var batches = builder.GetOrderedBatches();
        var insertBatches = batches.Where(b => b.OperationType == OperationType.Insert).ToList();

        var studentIdx = insertBatches.FindIndex(b => b.TableName == "students");
        var courseIdx = insertBatches.FindIndex(b => b.TableName == "courses");
        var joinIdx = insertBatches.FindIndex(b => b.TableName == "student_course");

        Assert.True(studentIdx >= 0 && courseIdx >= 0 && joinIdx >= 0,
            "All three batches must be present");
        Assert.True(joinIdx > studentIdx, "join table must come after students");
        Assert.True(joinIdx > courseIdx, "join table must come after courses");
    }

    [Fact]
    public void Given_M2MDelete_When_GetOrderedBatches_Then_JoinTableBatchBeforeBothEntityBatches()
    {
        var course = new Course { Id = 1, Title = "Maths" };
        var student = new Student
        {
            Id = 2,
            Name = "Alice",
            Courses = new List<Course> { course }
        };

        var builder = new ChangeSetBuilder();
        builder.Delete(student);

        var batches = builder.GetOrderedBatches();
        var deleteBatches = batches.Where(b => b.OperationType == OperationType.Delete).ToList();

        var studentIdx = deleteBatches.FindIndex(b => b.TableName == "students");
        var joinIdx = deleteBatches.FindIndex(b => b.TableName == "student_course");

        Assert.True(joinIdx >= 0 && studentIdx >= 0);
        Assert.True(joinIdx < studentIdx, "join table must come before students");
    }

    // -------------------------------------------------------------------------
    // GetOrderedBatches — batching rows for the same table
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_MultipleRowsSameTable_When_GetOrderedBatches_Then_CollapsedIntoOneBatch()
    {
        var builder = new ChangeSetBuilder();
        builder.Save(new Customer { Id = 0, Name = "Alice" });
        builder.Save(new Customer { Id = 0, Name = "Bob" });

        var batches = builder.GetOrderedBatches();
        var customerBatch = batches.Single(b => b.TableName == "customers");

        Assert.Equal(2, customerBatch.Rows.Count);
    }

    // -------------------------------------------------------------------------
    // M2M join row contents
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_M2MRelationship_When_Save_Then_JoinRowContainsCorrectForeignKeyValues()
    {
        // Non-default PKs so we can assert specific values in the join row.
        var course = new Course { Id = 10, Title = "Physics" };
        var student = new Student { Id = 5, Name = "Bob", Courses = new List<Course> { course } };

        var builder = new ChangeSetBuilder();
        builder.Save(student);

        var joinOp = builder.PendingOperations.Single(o => o.TableName == "student_course");

        Assert.Contains(joinOp.Row.Cells, c => c.ColumnName == "StudentId" && Equals(c.Value, 5));
        Assert.Contains(joinOp.Row.Cells, c => c.ColumnName == "CourseId" && Equals(c.Value, 10));
    }

    // -------------------------------------------------------------------------
    // Dirty tracking
    // -------------------------------------------------------------------------

    [Fact]
    public void Given_SnapshotAndOneFieldChanged_When_Save_Then_UpdateCellsContainOnlyDirtyField()
    {
        var builder = new ChangeSetBuilder();
        var entity = new Customer { Id = 1, Name = "Alice" };

        // Simulate GetAsync: snapshot the original state.
        builder.TrackSnapshot(entity);

        // Only change Name.
        entity.Name = "Alice-Updated";
        builder.Save(entity);

        var op = builder.PendingOperations.Single(o => o.TableName == "customers");
        Assert.Equal(OperationType.Update, op.Type);

        // SET clause must contain Id (key) + Name (dirty) only — not duplicates, not extra.
        Assert.Contains(op.Row.Cells, c => c.ColumnName == "Id");
        Assert.Contains(op.Row.Cells, c => c.ColumnName == "Name" && Equals(c.Value, "Alice-Updated"));
        Assert.Equal(2, op.Row.Cells.Count);
    }

    [Fact]
    public void Given_NoSnapshot_When_Save_Then_UpdateContainsAllColumns()
    {
        var builder = new ChangeSetBuilder();
        // No TrackSnapshot call — detached entity.
        builder.Save(new Customer { Id = 2, Name = "Bob" });

        var op = builder.PendingOperations.Single(o => o.TableName == "customers");
        Assert.Equal(OperationType.Update, op.Type);

        // Full UPDATE: all columns present.
        Assert.Equal(2, op.Row.Cells.Count); // Id + Name
        Assert.Contains(op.Row.Cells, c => c.ColumnName == "Id");
        Assert.Contains(op.Row.Cells, c => c.ColumnName == "Name");
    }

    [Fact]
    public void Given_SnapshotAndNothingChanged_When_Save_Then_NoPendingOperations()
    {
        var builder = new ChangeSetBuilder();
        var entity = new Customer { Id = 3, Name = "Carol" };

        builder.TrackSnapshot(entity);
        // Save without changing anything.
        builder.Save(entity);

        // No-op UPDATE must be suppressed.
        Assert.Empty(builder.PendingOperations.Where(o => o.TableName == "customers"));
    }
}
