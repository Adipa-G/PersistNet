using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PersistNet;
using PersistNet.DbAbstraction;
using PersistNet.Schema;
using Xunit;

namespace PersistNet.Tests;

public class SqliteSchemaTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteSchema     _schema;

    public SqliteSchemaTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _schema = new SqliteSchema(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static SchemaColumn Col(string name, string dbType, bool nullable = true,
        bool autoIncrement = false, int? size = null, int? precision = null, int? scale = null)
        => new(name, dbType, nullable, autoIncrement, null, size, precision, scale);

    private static SchemaTable SimpleTable(string name, IEnumerable<SchemaColumn> cols,
        SchemaPrimaryKey? pk = null,
        IEnumerable<SchemaIndex>? indexes = null,
        IEnumerable<SchemaForeignKey>? fks = null)
        => new(name, null, pk, cols.ToList(), (indexes ?? []).ToList(), (fks ?? []).ToList());

    private async Task<SchemaTable?> GetTable(string tableName)
    {
        var snap = await _schema.GetCurrentSchemaAsync();
        return snap.Tables.FirstOrDefault(t =>
            string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    }

    // ── CreateTable + GetCurrentSchema round-trip ─────────────────────────

    [Fact]
    public async Task Given_SimpleTable_When_CreateTableAndReadBack_Then_ColumnsMatch()
    {
        var table = SimpleTable("Products",
            [Col("Id", "INTEGER", nullable: false), Col("Name", "VARCHAR", nullable: false), Col("Price", "DECIMAL")],
            pk: new SchemaPrimaryKey(null, ["Id"]));

        await _schema.CreateTableAsync(table);
        var result = await GetTable("Products");

        Assert.NotNull(result);
        Assert.Equal(3, result.Columns.Count);
        Assert.Contains(result.Columns, c => c.Name == "Id" && c.DbType == "INTEGER" && !c.IsNullable);
        Assert.Contains(result.Columns, c => c.Name == "Name" && c.DbType == "VARCHAR");
        Assert.Contains(result.Columns, c => c.Name == "Price" && c.DbType == "DECIMAL");
    }

    [Fact]
    public async Task Given_AutoIncrementPK_When_CreateTableAndReadBack_Then_AutoIncrementDetected()
    {
        var table = SimpleTable("Items",
            [Col("Id", "INTEGER", nullable: false, autoIncrement: true), Col("Label", "VARCHAR")]);

        await _schema.CreateTableAsync(table);
        var result = await GetTable("Items");

        Assert.NotNull(result);
        var idCol = result.Columns.Single(c => c.Name == "Id");
        Assert.True(idCol.IsAutoIncrement);
        Assert.NotNull(result.PrimaryKey);
        Assert.Contains("Id", result.PrimaryKey!.Columns);
    }

    [Fact]
    public async Task Given_CompositePK_When_CreateTableAndReadBack_Then_PKColumnsMatch()
    {
        var table = SimpleTable("OrderItems",
            [Col("OrderId", "INTEGER", nullable: false), Col("ItemId", "INTEGER", nullable: false), Col("Qty", "INTEGER")],
            pk: new SchemaPrimaryKey(null, ["OrderId", "ItemId"]));

        await _schema.CreateTableAsync(table);
        var result = await GetTable("OrderItems");

        Assert.NotNull(result?.PrimaryKey);
        Assert.Equal(new[] { "OrderId", "ItemId" }, result!.PrimaryKey!.Columns);
    }

    // ── DropTable ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_ExistingTable_When_DropTable_Then_TableIsGone()
    {
        var table = SimpleTable("Temp", [Col("Id", "INTEGER")]);
        await _schema.CreateTableAsync(table);

        await _schema.DropTableAsync("Temp", null);
        var result = await GetTable("Temp");

        Assert.Null(result);
    }

    // ── AddColumn ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_ExistingTable_When_AddColumn_Then_ColumnAppearsInSchema()
    {
        var table = SimpleTable("Cats", [Col("Id", "INTEGER")]);
        await _schema.CreateTableAsync(table);

        await _schema.AddColumnAsync("Cats", null, Col("Name", "VARCHAR", size: 100));
        var result = await GetTable("Cats");

        Assert.Contains(result!.Columns, c => c.Name == "Name" && c.DbType == "VARCHAR");
    }

    // ── DropColumn ────────────────────────────────────────────────────────

    [Fact]
    public async Task Given_TableWithTwoColumns_When_DropColumn_Then_ColumnIsRemoved()
    {
        var table = SimpleTable("Dogs", [Col("Id", "INTEGER"), Col("Breed", "VARCHAR")]);
        await _schema.CreateTableAsync(table);

        await _schema.DropColumnAsync("Dogs", null, "Breed");
        var result = await GetTable("Dogs");

        Assert.DoesNotContain(result!.Columns, c => c.Name == "Breed");
    }

    // ── AlterColumn (table recreation) ────────────────────────────────────

    [Fact]
    public async Task Given_NullableColumn_When_AlterColumnToNotNull_Then_NullabilityUpdated()
    {
        var table = SimpleTable("Birds",
            [Col("Id", "INTEGER", nullable: false), Col("Species", "VARCHAR", nullable: true)],
            pk: new SchemaPrimaryKey(null, ["Id"]));
        await _schema.CreateTableAsync(table);

        await _schema.AlterColumnAsync("Birds", null, Col("Species", "VARCHAR", nullable: false));
        var result = await GetTable("Birds");

        var species = result!.Columns.Single(c => c.Name == "Species");
        Assert.False(species.IsNullable);
    }

    // ── CreateIndex / DropIndex ────────────────────────────────────────────

    [Fact]
    public async Task Given_Table_When_CreateIndex_Then_IndexAppearsInSchema()
    {
        var table = SimpleTable("Fish", [Col("Id", "INTEGER"), Col("Name", "VARCHAR")]);
        await _schema.CreateTableAsync(table);

        await _schema.CreateIndexAsync("Fish", null, new SchemaIndex("idx_fish_name", ["Name"], false));
        var result = await GetTable("Fish");

        Assert.Contains(result!.Indexes, i => i.Name == "idx_fish_name");
    }

    [Fact]
    public async Task Given_ExistingIndex_When_DropIndex_Then_IndexIsGone()
    {
        var table = SimpleTable("Plants", [Col("Id", "INTEGER"), Col("Name", "VARCHAR")]);
        await _schema.CreateTableAsync(table);
        await _schema.CreateIndexAsync("Plants", null, new SchemaIndex("idx_plants_name", ["Name"], true));

        await _schema.DropIndexAsync("Plants", null, "idx_plants_name");
        var result = await GetTable("Plants");

        Assert.DoesNotContain(result!.Indexes, i => i.Name == "idx_plants_name");
    }

    // ── AddForeignKey (table recreation) ──────────────────────────────────

    [Fact]
    public async Task Given_TwoTables_When_AddForeignKey_Then_FKAppearsInSchema()
    {
        var orders = SimpleTable("Orders", [Col("Id", "INTEGER", nullable: false)],
            pk: new SchemaPrimaryKey(null, ["Id"]));
        var lines = SimpleTable("OrderLines",
            [Col("Id", "INTEGER", nullable: false), Col("OrderId", "INTEGER")],
            pk: new SchemaPrimaryKey(null, ["Id"]));

        await _schema.CreateTableAsync(orders);
        await _schema.CreateTableAsync(lines);

        var fk = new SchemaForeignKey(null, ["OrderId"], "Orders", null, ["Id"], null, null);
        await _schema.AddForeignKeyAsync("OrderLines", null, fk);

        var result = await GetTable("OrderLines");
        Assert.NotEmpty(result!.ForeignKeys);
        Assert.Contains(result.ForeignKeys, f => f.ToTable == "Orders");
    }

    // ── DropForeignKey (table recreation) ─────────────────────────────────

    [Fact]
    public async Task Given_TableWithNamedFK_When_DropForeignKey_Then_FKIsRemoved()
    {
        var parent = SimpleTable("Parent", [Col("Id", "INTEGER", nullable: false)],
            pk: new SchemaPrimaryKey(null, ["Id"]));
        var fk = new SchemaForeignKey("fk_child_parent", ["ParentId"], "Parent", null, ["Id"], null, null);
        var child = SimpleTable("Child",
            [Col("Id", "INTEGER", nullable: false), Col("ParentId", "INTEGER")],
            pk: new SchemaPrimaryKey(null, ["Id"]),
            fks: [fk]);

        await _schema.CreateTableAsync(parent);
        await _schema.CreateTableAsync(child);

        // SQLite does not preserve FK names — drop by ToTable reference name
        await _schema.DropForeignKeyAsync("Child", null, "Parent");

        var result = await GetTable("Child");
        Assert.Empty(result!.ForeignKeys);
    }

    // ── Nullability round-trip ─────────────────────────────────────────────

    [Fact]
    public async Task Given_MixedNullability_When_CreateAndReadBack_Then_NullabilityPreserved()
    {
        var table = SimpleTable("Nulls",
            [Col("A", "INTEGER", nullable: false), Col("B", "VARCHAR", nullable: true)]);
        await _schema.CreateTableAsync(table);
        var result = await GetTable("Nulls");

        Assert.False(result!.Columns.Single(c => c.Name == "A").IsNullable);
        Assert.True(result.Columns.Single(c => c.Name == "B").IsNullable);
    }

    // ── VarChar/Decimal with size/precision ───────────────────────────────

    [Fact]
    public async Task Given_VarcharWithSize_When_CreateAndReadBack_Then_SizePreserved()
    {
        var table = SimpleTable("Sized",
            [Col("Id", "INTEGER"), Col("Name", "VARCHAR", size: 200)]);
        await _schema.CreateTableAsync(table);
        var result = await GetTable("Sized");

        var col = result!.Columns.Single(c => c.Name == "Name");
        Assert.Equal(200, col.Size);
    }

    // ── Phase 4a: BuildAddForeignKeySql referential rules + GenerateDiffSql ──────────

    [Fact]
    public void Given_ForeignKey_CascadeRule_When_BuildAddForeignKey_Then_OnDeleteCascade()
    {
        var fk = new SchemaForeignKey("fk_test", ["OrderId"], "Orders", null, ["Id"],
            ReferentialRuleType.Cascade, null);
        var sql = _schema.BuildAddForeignKeySql("OrderLines", null, fk);
        Assert.Contains("ON DELETE CASCADE", sql);
        Assert.Contains("FOREIGN KEY", sql);
    }

    [Fact]
    public void Given_ForeignKey_SetNullRule_When_BuildAddForeignKey_Then_OnDeleteSetNull()
    {
        var fk = new SchemaForeignKey("fk_test", ["OrderId"], "Orders", null, ["Id"],
            ReferentialRuleType.SetNull, null);
        var sql = _schema.BuildAddForeignKeySql("OrderLines", null, fk);
        Assert.Contains("ON DELETE SET NULL", sql);
    }

    [Fact]
    public void Given_ForeignKey_RestrictRule_When_BuildAddForeignKey_Then_OnDeleteRestrict()
    {
        var fk = new SchemaForeignKey("fk_test", ["OrderId"], "Orders", null, ["Id"],
            ReferentialRuleType.Restrict, null);
        var sql = _schema.BuildAddForeignKeySql("OrderLines", null, fk);
        Assert.Contains("ON DELETE RESTRICT", sql);
    }

    [Fact]
    public void Given_ForeignKey_NoActionRule_When_BuildAddForeignKey_Then_OnDeleteNoAction()
    {
        var fk = new SchemaForeignKey("fk_test", ["OrderId"], "Orders", null, ["Id"],
            ReferentialRuleType.DoNothing, null);
        var sql = _schema.BuildAddForeignKeySql("OrderLines", null, fk);
        Assert.Contains("ON DELETE NO ACTION", sql);
    }

    [Fact]
    public void Given_ForeignKey_WithOnUpdateCascade_When_BuildAddForeignKey_Then_OnUpdateClausePresent()
    {
        var fk = new SchemaForeignKey("fk_test", ["OrderId"], "Orders", null, ["Id"],
            null, ReferentialRuleType.Cascade);
        var sql = _schema.BuildAddForeignKeySql("OrderLines", null, fk);
        Assert.Contains("ON UPDATE CASCADE", sql);
        Assert.DoesNotContain("ON DELETE", sql);
    }

    [Fact]
    public void Given_MultiColumnIndex_When_BuildCreateIndex_Then_AllColumnsPresent()
    {
        var idx = new SchemaIndex("idx_composite", ["LastName", "FirstName"], false);
        var sql = _schema.BuildCreateIndexSql("Persons", null, idx);
        Assert.Contains("\"LastName\"", sql);
        Assert.Contains("\"FirstName\"", sql);
        Assert.Contains("idx_composite", sql);
        Assert.DoesNotContain("UNIQUE", sql);
    }

    [Fact]
    public void Given_SchemaDiff_TablesToCreate_When_SqliteGenerateDiffSql_Then_CreateTableStatement()
    {
        var table = SimpleTable("NewTable", [Col("Id", "INTEGER", nullable: false)],
            pk: new SchemaPrimaryKey(null, ["Id"]));
        var diff = new SchemaDiff([table], [], [], [], [], [], [], [], []);
        var snap = new SchemaSnapshot([]);

        var sql = _schema.GenerateDiffSql(diff, snap, snap);

        Assert.Single(sql);
        Assert.Contains("CREATE TABLE", sql[0]);
        Assert.Contains("\"NewTable\"", sql[0]);
    }

    [Fact]
    public void Given_SchemaDiff_ColumnsToAdd_When_SqliteGenerateDiffSql_Then_AddColumnStatement()
    {
        var diff = new SchemaDiff([], [], [("Items", null, Col("Rating", "INTEGER"))], [], [], [], [], [], []);
        var snap = new SchemaSnapshot([]);

        var sql = _schema.GenerateDiffSql(diff, snap, snap);

        Assert.Single(sql);
        Assert.Contains("ADD COLUMN", sql[0]);
        Assert.Contains("\"Rating\"", sql[0]);
    }

    [Fact]
    public void Given_SchemaDiff_IndexesToDrop_When_SqliteGenerateDiffSql_Then_DropIndexStatement()
    {
        var diff = new SchemaDiff([], [], [], [], [], [], [("Items", null, "idx_items_name")], [], []);
        var snap = new SchemaSnapshot([]);

        var sql = _schema.GenerateDiffSql(diff, snap, snap);

        Assert.Single(sql);
        Assert.Contains("DROP INDEX", sql[0]);
        Assert.Contains("\"idx_items_name\"", sql[0]);
        Assert.DoesNotContain(" ON ", sql[0]); // SQLite drops index without table reference
    }
}
