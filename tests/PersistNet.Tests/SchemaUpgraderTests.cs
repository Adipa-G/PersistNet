using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PersistNet.Tests;

public class SchemaUpgraderTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SchemaUpgraderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ── Test entity types ──────────────────────────────────────────────────

    [TableInfo(TableName = "Products")]
    private class Product
    {
        [ColumnInfo(Key = true, AutoIncrement = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 200, Nullable = false)]
        public string Name { get; set; } = "";

        [ColumnInfo(ColumnType = ColumnType.Decimal, Precision = 10, Scale = 2)]
        public decimal Price { get; set; }
    }

    [TableInfo(TableName = "Categories")]
    private class Category
    {
        [ColumnInfo(Key = true, AutoIncrement = true, ColumnType = ColumnType.Integer)]
        public int Id { get; set; }

        [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
        public string Label { get; set; } = "";
    }

    // Not decorated with [TableInfo] — must be ignored by FromAssembly
    private class NotAnEntity
    {
        public int Value { get; set; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private SchemaUpgrader UpgraderFor(params Type[] types)
        => SchemaUpgrader.ForTypes(_connection, DbProvider.SQLite, types);

    private SchemaUpgrader UpgraderFromAssembly(Func<Type, bool>? filter = null)
        // Scope to types declared inside this class to avoid picking up entity types
        // from other test files (e.g. those that carry a Schema = "zoo" attribute).
        => SchemaUpgrader.FromAssembly(_connection, DbProvider.SQLite,
            typeof(SchemaUpgraderTests).Assembly,
            t => t.DeclaringType == typeof(SchemaUpgraderTests) && (filter?.Invoke(t) ?? true));

    // ── IsUpToDateAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task Given_EmptyDatabase_When_IsUpToDate_Then_ReturnsFalse()
    {
        var upgrader = UpgraderFor(typeof(Product));
        Assert.False(await upgrader.IsUpToDateAsync());
    }

    [Fact]
    public async Task Given_DatabaseAfterApply_When_IsUpToDate_Then_ReturnsTrue()
    {
        var upgrader = UpgraderFor(typeof(Product));
        await upgrader.ApplyAsync();
        Assert.True(await upgrader.IsUpToDateAsync());
    }

    // ── ApplyAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_EmptyDatabase_When_Apply_Then_TableExists()
    {
        var upgrader = UpgraderFor(typeof(Product));
        await upgrader.ApplyAsync();

        // Verify table exists via a fresh SqliteSchema
        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap   = await schema.GetCurrentSchemaAsync();

        Assert.Contains(snap.Tables, t => string.Equals(t.Name, "Products", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Given_DatabaseAfterApply_When_ApplyAgain_Then_IsIdempotent()
    {
        var upgrader = UpgraderFor(typeof(Product));
        await upgrader.ApplyAsync();
        var ex = await Record.ExceptionAsync(() => upgrader.ApplyAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Given_MultipleEntityTypes_When_Apply_Then_AllTablesCreated()
    {
        var upgrader = UpgraderFor(typeof(Product), typeof(Category));
        await upgrader.ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap   = await schema.GetCurrentSchemaAsync();
        var names  = snap.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Products", names);
        Assert.Contains("Categories", names);
    }

    // ── ExportMigrationSqlAsync ────────────────────────────────────────────

    [Fact]
    public async Task Given_EmptyDatabase_When_ExportMigrationSql_Then_ContainsCreateTable()
    {
        var upgrader = UpgraderFor(typeof(Product));
        var sql = await upgrader.ExportMigrationSqlAsync();

        Assert.NotEmpty(sql);
        Assert.Contains(sql, s => s.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Given_UpToDateDatabase_When_ExportMigrationSql_Then_ReturnsEmpty()
    {
        var upgrader = UpgraderFor(typeof(Product));
        await upgrader.ApplyAsync();

        var sql = await upgrader.ExportMigrationSqlAsync();
        Assert.Empty(sql);
    }

    [Fact]
    public async Task Given_PendingChanges_When_ExportMigrationSql_Then_SqlNotApplied()
    {
        // Export should not affect the live DB
        var upgrader = UpgraderFor(typeof(Product));
        await upgrader.ExportMigrationSqlAsync();

        // DB should still be empty
        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap   = await schema.GetCurrentSchemaAsync();
        Assert.DoesNotContain(snap.Tables, t => t.Name == "Products");
    }

    // ── FromAssembly filtering ─────────────────────────────────────────────

    [Fact]
    public async Task Given_Assembly_When_FromAssembly_Then_OnlyTableInfoTypesIncluded()
    {
        // Apply all [TableInfo] types from this assembly
        var upgrader = UpgraderFromAssembly();
        await upgrader.ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap   = await schema.GetCurrentSchemaAsync();
        var names  = snap.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // NotAnEntity has no [TableInfo] — should NOT produce a table
        Assert.DoesNotContain("NotAnEntity", names);

        // Product and Category should be present
        Assert.Contains("Products", names);
        Assert.Contains("Categories", names);
    }

    [Fact]
    public async Task Given_Assembly_When_FromAssemblyWithFilter_Then_FilterApplied()
    {
        // Only include Category
        var upgrader = UpgraderFromAssembly(filter: t => t == typeof(Category));
        await upgrader.ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap   = await schema.GetCurrentSchemaAsync();
        var names  = snap.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Categories", names);
        Assert.DoesNotContain("Products", names);
    }

    // ── Guard conditions ───────────────────────────────────────────────────

    [Fact]
    public void ForTypes_UnknownDbProvider_ThrowsArgumentOutOfRangeException()
    {
        // CreateSchema throws for any DbProvider value not handled in the switch.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SchemaUpgrader.ForTypes(_connection, (DbProvider)999, Array.Empty<Type>()));
    }

    [Fact]
    public void FromAssembly_NullFilter_IncludesAllTableInfoTypesInAssembly()
    {
        // Passing filter = null uses the `_ => true` default path inside FromAssembly.
        // We just verify creation succeeds (the result includes every [TableInfo] type).
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var upgrader = SchemaUpgrader.FromAssembly(conn, DbProvider.SQLite,
            typeof(SchemaUpgraderTests).Assembly);

        Assert.NotNull(upgrader);
    }

    // ── Schema migration: add column ──────────────────────────────────────

    // Nested so DeclaringType != SchemaUpgraderTests and the UpgraderFromAssembly
    // scan filter (t.DeclaringType == typeof(SchemaUpgraderTests)) ignores them,
    // preventing duplicate-table-name conflicts in the assembly-scan tests.
    private static class MigrationFixtures
    {
        [TableInfo(TableName = "mig_items")]
        public class V1
        {
            [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
            public int Id { get; set; }

            [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
            public string Name { get; set; } = "";
        }

        [TableInfo(TableName = "mig_items")]
        public class V2
        {
            [ColumnInfo(Key = true, ColumnType = ColumnType.Integer)]
            public int Id { get; set; }

            [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
            public string Name { get; set; } = "";

            [ColumnInfo(ColumnType = ColumnType.Integer, Nullable = true)]
            public int? Score { get; set; }
        }
    }

    [Fact]
    public async Task Given_ExistingTable_When_ColumnAdded_Then_ApplyAddsMissingColumn()
    {
        // v1 schema: mig_items (Id, Name)
        await UpgraderFor(typeof(MigrationFixtures.V1)).ApplyAsync();

        // v2 schema: mig_items (Id, Name, Score) — Score is new
        await UpgraderFor(typeof(MigrationFixtures.V2)).ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap = await schema.GetCurrentSchemaAsync();
        var table = snap.Tables.First(t => string.Equals(t.Name, "mig_items", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(table.Columns, c => string.Equals(c.Name, "Score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Given_ExistingTable_When_ColumnAdded_Then_ExportSqlContainsAlterTable()
    {
        // v1 schema: mig_items (Id, Name)
        await UpgraderFor(typeof(MigrationFixtures.V1)).ApplyAsync();

        // v2 schema should produce ADD COLUMN SQL without applying it
        var sql = await UpgraderFor(typeof(MigrationFixtures.V2)).ExportMigrationSqlAsync();

        Assert.NotEmpty(sql);
        Assert.Contains(sql, s => s.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
                                  && s.Contains("Score", StringComparison.OrdinalIgnoreCase));
    }

    // ── Schema migration: drop table ──────────────────────────────────────

    [Fact]
    public async Task Given_ObsoleteTable_When_Apply_Then_TableIsDropped()
    {
        // Create a table that no entity type is registered for.
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE obsolete_items (Id INTEGER NOT NULL PRIMARY KEY)";
            await cmd.ExecuteNonQueryAsync();
        }

        // Apply the MigItemV1 schema — SchemaUpgrader sees obsolete_items as not in desired → drops it.
        await UpgraderFor(typeof(MigrationFixtures.V1)).ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap = await schema.GetCurrentSchemaAsync();
        Assert.DoesNotContain(snap.Tables, t =>
            string.Equals(t.Name, "obsolete_items", StringComparison.OrdinalIgnoreCase));
    }

    // ── FK schema generation ──────────────────────────────────────────────

    // Nested to exclude from UpgraderFromAssembly scan.
    private static class FkFixtures
    {
        [TableInfo(TableName = "fk_depts")]
        public class FkDept
        {
            [ColumnInfo(Key = true, AutoIncrement = true, ColumnType = ColumnType.Integer)]
            public int Id { get; set; }

            [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
            public string Name { get; set; } = "";

            [OneToManyRelationshipInfo(RelatedType = typeof(FkMember), MappedBy = "Dept")]
            public List<FkMember>? Members { get; set; }
        }

        [TableInfo(TableName = "fk_members")]
        public class FkMember
        {
            [ColumnInfo(Key = true, AutoIncrement = true, ColumnType = ColumnType.Integer)]
            public int Id { get; set; }

            [ColumnInfo(ColumnType = ColumnType.Integer)]
            public int DeptId { get; set; }

            [ColumnInfo(ColumnType = ColumnType.Varchar, Size = 100, Nullable = false)]
            public string Name { get; set; } = "";

            [ManyToOneRelationshipInfo(
                RelatedType = typeof(FkDept),
                FromKeys = new[] { "DeptId" },
                ToKeys = new[] { "Id" })]
            public FkDept? Dept { get; set; }
        }
    }

    [Fact]
    public async Task Given_O2MEntities_When_ApplyAsync_Then_SchemaContainsForeignKey()
    {
        await UpgraderFor(typeof(FkFixtures.FkDept), typeof(FkFixtures.FkMember)).ApplyAsync();

        var schema = new PersistNet.DbAbstraction.SqliteSchema(_connection);
        var snap = await schema.GetCurrentSchemaAsync();

        var membersTable = snap.Tables.First(t =>
            string.Equals(t.Name, "fk_members", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(membersTable.ForeignKeys);
        Assert.Contains(membersTable.ForeignKeys, fk =>
            string.Equals(fk.ToTable, "fk_depts", StringComparison.OrdinalIgnoreCase));
    }
}
