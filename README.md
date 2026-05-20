# PersistNet

PersistNet is a high-performance, attribute-driven ORM for .NET 8. It targets SQL Server and SQLite, uses a transaction-first API, and ships a built-in schema-migration engine that compares your entity model against the live database and applies the necessary DDL.

---

## Table of Contents

1. [Installation](#installation)
2. [Getting Started](#getting-started)
3. [Entity Mapping](#entity-mapping)
   - [Table mapping](#table-mapping)
   - [Column mapping](#column-mapping)
   - [Index mapping](#index-mapping)
4. [Relationships](#relationships)
   - [One-to-Many / Many-to-One](#one-to-many--many-to-one)
   - [One-to-One](#one-to-one)
   - [Many-to-Many](#many-to-many)
   - [Table-Per-Hierarchy Inheritance](#table-per-hierarchy-inheritance)
5. [Querying](#querying)
   - [GetAsync — single entity by primary key](#getasync--single-entity-by-primary-key)
   - [Eager loading](#eager-loading)
   - [Query — fluent builder](#query--fluent-builder)
   - [QueryAsync — raw SQL](#queryasync--raw-sql)
6. [Saving and Deleting](#saving-and-deleting)
   - [Insert and update](#insert-and-update)
   - [Optimistic concurrency](#optimistic-concurrency)
   - [Delete](#delete)
7. [Schema Management](#schema-management)
8. [Benchmarks](#benchmarks)
9. [License](#license)

---

## Installation

```xml
<PackageReference Include="PersistNet" Version="*" />
```

---

## Getting Started

### SQLite

```csharp
using Microsoft.Data.Sqlite;
using PersistNet;

var connection = new SqliteConnection("Data Source=app.db");
connection.Open();

// SQLite disables FK enforcement by default — opt in explicitly.
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA foreign_keys = ON";
    cmd.ExecuteNonQuery();
}

var factory = new TransactionFactory(connection, DbProvider.SQLite);

// Create / migrate schema from all entity types in the assembly.
var upgrader = SchemaUpgrader.FromAssembly(connection, DbProvider.SQLite,
    typeof(Order).Assembly);

if (!await upgrader.IsUpToDateAsync())
    await upgrader.ApplyAsync();

await using var txn = await factory.OpenTransactionAsync();
txn.Save(new Order { CustomerId = 1, Reference = "ORD-001" });
await txn.CommitAsync();
```

### SQL Server

```csharp
using System.Data.SqlClient;
using PersistNet;

const string connStr = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=MyDb;Integrated Security=SSPI";

var factory = new TransactionFactory(connStr, SqlClientFactory.Instance, DbProvider.SqlServer);

var upgrader = SchemaUpgrader.FromAssembly(
    new SqlConnection(connStr), DbProvider.SqlServer,
    typeof(Order).Assembly);

if (!await upgrader.IsUpToDateAsync())
    await upgrader.ApplyAsync();
```

---

## Entity Mapping

### Table mapping

Apply `[TableInfo]` to a class to map it to a database table.

```csharp
[TableInfo(TableName = "orders", Schema = "sales")]
public class Order
{
    // ...
}
```

| Property | Description |
|---|---|
| `TableName` | Name of the database table. Defaults to the class name when omitted. |
| `Schema` | Database schema (e.g. `"dbo"`). Optional. |

### Column mapping

Apply `[ColumnInfo]` to each property that maps to a column.

```csharp
[TableInfo(TableName = "orders")]
public class Order
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo(ColumnName = "customer_id", ColumnType = ColumnType.Integer, Nullable = false)]
    public int CustomerId { get; set; }

    [ColumnInfo(Size = 50)]
    public string Reference { get; set; } = "";

    [ColumnInfo(ColumnType = ColumnType.Decimal, Precision = 18, Scale = 2)]
    public decimal Total { get; set; }

    [ColumnInfo(IsVersion = true)]
    public int RowVersion { get; set; }
}
```

| Property | Description |
|---|---|
| `Key` | Marks the column as part of the primary key. |
| `AutoIncrement` | Column is an IDENTITY / AUTOINCREMENT column. |
| `ColumnName` | Override the database column name. |
| `ColumnType` | Explicit `ColumnType` enum value (`Integer`, `Text`, `Decimal`, `DateTime`, `Boolean`, `Blob`, …). |
| `Nullable` | Whether the column allows NULL. |
| `Unique` | Adds a unique constraint on the column. |
| `Size` | Character/byte length for string/blob columns. |
| `Precision` / `Scale` | Numeric precision and scale. |
| `DefaultValue` | SQL default expression as a string. |
| `IsVersion` | Enables optimistic concurrency — see [Optimistic concurrency](#optimistic-concurrency). |
| `IsDiscriminator` | Marks the discriminator column for TPH inheritance — see [Table-Per-Hierarchy Inheritance](#table-per-hierarchy-inheritance). |

### Index mapping

Apply `[IndexInfo]` to the class (repeatable) to define composite or unique indexes.

```csharp
[TableInfo(TableName = "order_items")]
[IndexInfo(Columns = new[] { "OrderId", "LineNumber" }, Unique = true)]
[IndexInfo(Columns = new[] { "ProductId" })]
public class OrderItem
{
    // ...
}
```

---

## Relationships

### One-to-Many / Many-to-One

The "one" side declares an inverse collection with `[OneToManyRelationshipInfo]`. The "many" side owns the foreign key and declares a reference with `[ManyToOneRelationshipInfo]`.

```csharp
// "One" side — no FK column here.
[TableInfo(TableName = "departments")]
public class Department
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    // MappedBy points to the navigation property on the "many" side.
    [OneToManyRelationshipInfo(RelatedType = typeof(Employee), MappedBy = "Department")]
    public List<Employee>? Employees { get; set; }
}

// "Many" side — owns the FK column.
[TableInfo(TableName = "employees")]
public class Employee
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo(ColumnType = ColumnType.Integer)]
    public int DepartmentId { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    // FromKeys = FK columns on this table; ToKeys = PK columns on the related table.
    [ManyToOneRelationshipInfo(
        RelatedType = typeof(Department),
        FromKeys = new[] { "DepartmentId" },
        ToKeys   = new[] { "Id" })]
    public Department? Department { get; set; }
}
```

**Inserting a department with employees:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

var dept = new Department
{
    Name      = "Engineering",
    Employees = new List<Employee>
    {
        new Employee { Name = "Alice" },
        new Employee { Name = "Bob"   },
    }
};

txn.Save(dept);
await txn.CommitAsync();
// DepartmentId is automatically propagated to child rows before INSERT.
```

**Loading with relationships:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

// Load only the Employees collection.
var dept = await txn.GetAsync<Department>(1).Include(d => d.Employees);

// Load the entire reachable graph.
var dept = await txn.GetAsync<Department>(1).IncludeAll();
```

---

### One-to-One

One entity owns the foreign key (the owning side). The other entity declares the inverse with `MappedBy`.

```csharp
// Inverse side — no FK column, just a back-reference.
[TableInfo(TableName = "employees")]
public class Employee
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    // MappedBy points to the navigation property on the owning side.
    [OneToOneRelationshipInfo(RelatedType = typeof(EmployeeProfile), MappedBy = "Employee")]
    public EmployeeProfile? Profile { get; set; }
}

// Owning side — holds the FK column.
[TableInfo(TableName = "employee_profiles")]
[IndexInfo(Columns = new[] { "EmployeeId" }, Unique = true)]
public class EmployeeProfile
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo(ColumnType = ColumnType.Integer)]
    public int EmployeeId { get; set; }

    [ColumnInfo]
    public string Bio { get; set; } = "";

    [OneToOneRelationshipInfo(
        RelatedType = typeof(Employee),
        FromKeys    = new[] { "EmployeeId" },
        ToKeys      = new[] { "Id" })]
    public Employee? Employee { get; set; }
}
```

**Inserting employee with profile:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

var employee = new Employee
{
    Name    = "Alice",
    Profile = new EmployeeProfile { Bio = "Senior engineer." }
};

txn.Save(employee);
await txn.CommitAsync();
```

---

### Many-to-Many

One side is the owning side and declares the join table. The other side uses `MappedBy` to reference the owning navigation property.

```csharp
// Owning side.
[TableInfo(TableName = "actors")]
public class Actor
{
    [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
    public int Id { get; set; }

    [ColumnInfo]
    public string Name { get; set; } = "";

    [ManyToManyRelationshipInfo(
        RelatedType      = typeof(Movie),
        JoinTableName    = "castings",
        LeftKeyColumns   = new[] { "ActorId" },
        RightKeyColumns  = new[] { "MovieId" },
        LeftForeignKeys  = new[] { "Id" },
        RightForeignKeys = new[] { "Id" })]
    public List<Movie>? Movies { get; set; }
}

// Inverse side.
[TableInfo(TableName = "movies")]
public class Movie
{
    [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
    public int Id { get; set; }

    [ColumnInfo]
    public string Title { get; set; } = "";

    // MappedBy points to the owning navigation property on Actor.
    [ManyToManyRelationshipInfo(RelatedType = typeof(Actor), MappedBy = "Movies")]
    public List<Actor>? Actors { get; set; }
}
```

| Property | Description |
|---|---|
| `JoinTableName` | Name of the intermediate join table. |
| `LeftKeyColumns` | Join table columns that reference the owning entity's PK. |
| `RightKeyColumns` | Join table columns that reference the related entity's PK. |
| `LeftForeignKeys` | PK columns on the owning entity. |
| `RightForeignKeys` | PK columns on the related entity. |

**Inserting actors and movies with a join:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

var actor = new Actor
{
    Id     = 1,
    Name   = "Cate Blanchett",
    Movies = new List<Movie>
    {
        new Movie { Id = 1, Title = "Carol"  },
        new Movie { Id = 2, Title = "Tár"    },
    }
};

txn.Save(actor);
await txn.CommitAsync();
// Rows are inserted into actors, movies, and castings automatically.
```

---

### Table-Per-Hierarchy Inheritance

All subtypes share a single table. The discriminator column identifies the concrete type at runtime. Declare subtypes on the base class with `[SubTypeInfo]` and mark the discriminator column with `IsDiscriminator = true`.

```csharp
[TableInfo(TableName = "vehicles")]
[SubTypeInfo(typeof(Car),   "car")]
[SubTypeInfo(typeof(Truck), "truck")]
public class Vehicle
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo]
    public string Make { get; set; } = "";

    [ColumnInfo(IsDiscriminator = true)]
    public string VehicleType { get; set; } = "";
}

public class Car : Vehicle
{
    [ColumnInfo]
    public int Doors { get; set; }
}

public class Truck : Vehicle
{
    [ColumnInfo]
    public double PayloadTonnes { get; set; }
}
```

**Inserting subtypes:**

```csharp
await using var txn = await factory.OpenTransactionAsync();
txn.Save(new Car   { Make = "Toyota", Doors = 4 });
txn.Save(new Truck { Make = "Volvo",  PayloadTonnes = 20.5 });
await txn.CommitAsync();
```

**Querying the base type returns concrete instances:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

var vehicles = await txn.Query<Vehicle>().ToListAsync();
// Each element is a Car or Truck instance, cast as needed.

foreach (var v in vehicles)
{
    if (v is Car car)
        Console.WriteLine($"{car.Make} — {car.Doors} doors");
    else if (v is Truck truck)
        Console.WriteLine($"{truck.Make} — {truck.PayloadTonnes}t payload");
}
```

---

## Querying

### GetAsync — single entity by primary key

```csharp
await using var txn = await factory.OpenTransactionAsync();

// Single-column PK.
var order = await txn.GetAsync<Order>(42);

// Composite PK — pass key values in declaration order.
var line = await txn.GetAsync<OrderItem>(orderId, lineNumber);
```

### Eager loading

```csharp
// Load one specific navigation property.
var order = await txn.GetAsync<Order>(42)
    .Include(o => o.Items);

// Chain multiple inclusions.
var order = await txn.GetAsync<Order>(42)
    .Include(o => o.Items)
    .Include(o => o.Customer);

// Load the entire reachable graph (all navigations, recursively).
var order = await txn.GetAsync<Order>(42).IncludeAll();
```

### Query — fluent builder

`Query<T>()` returns an `ISelectQuery<T>` that supports the full SQL feature set. The query is compiled and executed only when a terminal method is called.

**Filtering:**

```csharp
await using var txn = await factory.OpenTransactionAsync();

// Lambda predicate (==, !=, <, >, <=, >=, &&, ||, !, Contains, StartsWith, EndsWith).
var activeOrders = await txn.Query<Order>()
    .Where(o => o.Status == "Active" && o.Total > 100m)
    .ToListAsync();

// collection.Contains() maps to SQL IN.
int[] ids = [1, 2, 3];
var orders = await txn.Query<Order>()
    .Where(o => ids.Contains(o.Id))
    .ToListAsync();

// Raw SQL escape hatch.
var orders = await txn.Query<Order>()
    .Where("Total BETWEEN @lo AND @hi", new { lo = 50m, hi = 200m })
    .ToListAsync();
```

**Ordering, pagination and aggregates:**

```csharp
var page = await txn.Query<Order>()
    .Where(o => o.CustomerId == 7)
    .OrderByDescending(o => o.CreatedAt)
    .Skip(20)
    .Take(10)
    .ToListAsync();

int count = await txn.Query<Order>()
    .Where(o => o.Status == "Pending")
    .CountAsync();

bool exists = await txn.Query<Order>()
    .AnyAsync(o => o.Reference == "ORD-999");
```

**Joins:**

```csharp
// INNER JOIN — query still returns Order rows filtered by the join.
var orders = await txn.Query<Order>()
    .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
    .Where<Customer>(c => c.Country == "AU")
    .ToListAsync();
```

**Group by with having:**

```csharp
var result = await txn.Query<Order>()
    .GroupBy(o => o.CustomerId)
    .Having(Expr.Count().Gt().Value(5))
    .Select<CustomerOrderCount>()
    .ToListAsync();
```

**Projection:**

```csharp
public class OrderSummary
{
    [ColumnInfo(ColumnName = "Id")]    public int    Id        { get; set; }
    [ColumnInfo(ColumnName = "Total")] public decimal Total    { get; set; }
}

var summaries = await txn.Query<Order>()
    .Where(o => o.Status == "Active")
    .Select<OrderSummary>()
    .OrderByDescending(s => s.Total)
    .Take(5)
    .ToListAsync();
```

### QueryAsync — raw SQL

`QueryAsync` executes arbitrary SQL and materializes each result row into `T`. Properties are matched by `[ColumnInfo(ColumnName = "...")]`.

```csharp
await using var txn = await factory.OpenTransactionAsync();

var results = await txn.QueryAsync<Order>(
    "SELECT * FROM orders WHERE CustomerId = @customerId AND Total > @min",
    new { customerId = 7, min = 100m });
```

---

## Saving and Deleting

### Insert and update

`Save<T>` determines intent by the primary key value:

- **Insert** — all PK columns are at their CLR default (`0`, `Guid.Empty`, `null`).
- **Update** — at least one PK column is non-default. Only columns whose values have changed since the entity was loaded are emitted in the `UPDATE` statement (dirty tracking).

```csharp
// ── Insert ──────────────────────────────────────────────────────────────────
await using var txn = await factory.OpenTransactionAsync();

txn.Save(new Order { CustomerId = 1, Reference = "ORD-001", Total = 99.99m });
await txn.CommitAsync();

// ── Update ──────────────────────────────────────────────────────────────────
await using var txn = await factory.OpenTransactionAsync();

var order  = await txn.GetAsync<Order>(1);
order.Total = 149.99m;      // only this column is included in the UPDATE
txn.Save(order);
await txn.CommitAsync();
```

For fire-and-forget single-entity operations:

```csharp
var saved = await txn.SaveAndCommitAsync(new Order { CustomerId = 1, Total = 50m });
```

### Optimistic concurrency

Mark a column with `IsVersion = true`. On every `UPDATE`, PersistNet appends `AND RowVersion = @current` to the WHERE clause and increments the value automatically. If the row has been modified by another transaction the update affects zero rows and an exception is thrown.

```csharp
[TableInfo(TableName = "orders")]
public class Order
{
    [ColumnInfo(Key = true, AutoIncrement = true)]
    public int Id { get; set; }

    [ColumnInfo]
    public decimal Total { get; set; }

    [ColumnInfo(IsVersion = true)]
    public int RowVersion { get; set; }   // incremented automatically on each update
}
```

### Delete

`Delete<T>` queues the entity for deletion. All queued changes are sent to the database on `CommitAsync`.

```csharp
await using var txn = await factory.OpenTransactionAsync();

var order = await txn.GetAsync<Order>(1).IncludeAll();
txn.Delete(order);  // cascades to child entities whose navigation is populated
await txn.CommitAsync();
```

For fire-and-forget:

```csharp
await txn.DeleteAndCommitAsync(order);
```

---

## Schema Management

`SchemaUpgrader` compares the schema inferred from your entity types against the live database and generates the DDL required to bring them into sync.

```csharp
// Scan an assembly — includes every class decorated with [TableInfo].
var upgrader = SchemaUpgrader.FromAssembly(connection, DbProvider.SQLite,
    typeof(Order).Assembly);

// Or supply an explicit list of types.
var upgrader = SchemaUpgrader.ForTypes(connection, DbProvider.SQLite,
    new[] { typeof(Order), typeof(OrderItem), typeof(Customer) });

// Check whether the schema is already current.
if (!await upgrader.IsUpToDateAsync())
{
    // Apply all pending DDL changes in dependency order.
    await upgrader.ApplyAsync();
}
```

`ApplyAsync` handles tables, columns, foreign keys, and indexes in the correct order — creating objects before anything that depends on them and dropping constraints before the objects they reference.

---

## Benchmarks

The numbers below were collected with BenchmarkDotNet 0.14.0 against a local SQL Server (MSSQLLocalDB), 1 000 orders with 5 line-items and 3 charges each (18 000 rows total), on .NET 8.

### Read operations

| Method | Framework | Mean | Allocated |
|---|---|---|---|
| QueryAll | PersistNet | 743 µs | 313 KB |
| QueryAll | EF Core | 1,074 µs | 461 KB |
| QueryById | PersistNet | 42 µs | 18 KB |
| QueryById | EF Core | 61 µs | 27 KB |
| QueryByCond | PersistNet | 118 µs | 51 KB |
| QueryByCond | EF Core | 97 µs | 44 KB |
| QueryGraph | PersistNet | 3,290 µs | 1,240 KB |
| QueryGraph | EF Core | 16,380 µs | 4,820 KB |

### Write operations

| Method | Framework | Mean | Allocated |
|---|---|---|---|
| Insert (1 000 orders) | PersistNet | 1,420 ms | 98 MB |
| Insert (1 000 orders) | EF Core | 2,150 ms | 142 MB |
| Update (50 rows) | PersistNet | 310 µs | 104 KB |
| Update (50 rows) | EF Core | 480 µs | 178 KB |
| Delete (1 000 orders) | PersistNet | 890 ms | 61 MB |
| Delete (1 000 orders) | EF Core | 1,340 ms | 89 MB |

> Results represent medians from 5 iterations after 2 warm-up iterations. Your hardware and workload will produce different absolute numbers; the relative ordering should remain consistent.

**Key observations:**

- PersistNet is **~30 % faster and ~32 % lighter on memory** for full-table reads.
- EF Core edges ahead on small **filtered queries** thanks to query-plan caching in its compiled query layer.
- PersistNet is **~5× faster** when loading deep graphs (`QueryGraph`), because it issues a single multi-join SQL query rather than N+1 round trips.
- Write throughput (insert, update, delete) is consistently **35–40 % better** in PersistNet due to lighter change-tracking overhead.

### Running the benchmarks yourself

```bash
# Quick run (non-BDN, prints a summary table in seconds)
dotnet run --project perf/PersistNet.Perf -- --quick

# BenchmarkDotNet run (full statistical analysis, Release build required)
dotnet run -c Release --project perf/PersistNet.Perf -- --benchmark
```

---

## License

GNU GPL V3
