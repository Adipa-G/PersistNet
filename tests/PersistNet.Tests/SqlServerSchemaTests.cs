using System.Collections.Generic;
using PersistNet.DbAbstraction;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// SQL string verification tests for <see cref="SqlServerSchema"/>.
/// No live database is needed — all tests call the Build*Sql helpers directly.
/// </summary>
public class SqlServerSchemaTests
{
    // Passing null! is safe because we only call Build*Sql methods which never touch the connection.
    private static SqlServerSchema Schema() => new(null!);

    // ── Helpers ────────────────────────────────────────────────────────────

    private static SchemaColumn Col(string name, string dbType, bool nullable = true,
        bool autoIncrement = false, int? size = null, int? precision = null, int? scale = null)
        => new(name, dbType, nullable, autoIncrement, null, size, precision, scale);

    private static SchemaTable TableWith(string name, IEnumerable<SchemaColumn> cols,
        SchemaPrimaryKey? pk = null, string? schema = null)
        => new(name, schema, pk, new List<SchemaColumn>(cols), [], []);

    // ── QuoteIdentifier ────────────────────────────────────────────────────

    [Fact]
    public void Given_TableName_When_BuildDropTable_Then_UsesBracketQuoting()
    {
        var sql = Schema().BuildDropTableSql("Orders", null);
        Assert.Contains("[Orders]", sql);
    }

    [Fact]
    public void Given_TableWithSchema_When_BuildDropTable_Then_SchemaAlsoBracketed()
    {
        var sql = Schema().BuildDropTableSql("Orders", "dbo");
        Assert.Contains("[dbo].[Orders]", sql);
    }

    // ── IDENTITY(1,1) ──────────────────────────────────────────────────────

    [Fact]
    public void Given_AutoIncrementColumn_When_BuildCreateTable_Then_ContainsIdentity()
    {
        var table = TableWith("Items",
            [Col("Id", "INTEGER", nullable: false, autoIncrement: true)],
            pk: new SchemaPrimaryKey(null, ["Id"]));

        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("IDENTITY(1,1)", sql);
    }

    // ── Type mapping ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("GUID",      "UNIQUEIDENTIFIER")]
    [InlineData("BIGINT",    "BIGINT")]
    [InlineData("BOOLEAN",   "BIT")]
    [InlineData("CHAR",      "CHAR(1)")]
    [InlineData("INTEGER",   "INT")]
    [InlineData("DATE",      "DATE")]
    [InlineData("DOUBLE",    "FLOAT")]
    [InlineData("FLOAT",     "REAL")]
    [InlineData("TIMESTAMP", "DATETIME2")]
    public void Given_CanonicalType_When_BuildCreateTable_Then_SqlServerTypeUsed(string canonical, string expected)
    {
        var table = TableWith("T", [Col("Col", canonical)]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains(expected, sql);
    }

    [Fact]
    public void Given_VarcharWithSize_When_BuildCreateTable_Then_NVarcharWithSize()
    {
        var table = TableWith("T", [Col("Name", "VARCHAR", size: 100)]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("NVARCHAR(100)", sql);
    }

    [Fact]
    public void Given_VarcharNoSize_When_BuildCreateTable_Then_NVarcharMax()
    {
        var table = TableWith("T", [Col("Notes", "VARCHAR")]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("NVARCHAR(MAX)", sql);
    }

    [Fact]
    public void Given_DecimalWithPrecisionAndScale_When_BuildCreateTable_Then_DecimalPrecisionScale()
    {
        var table = TableWith("T", [Col("Price", "DECIMAL", precision: 10, scale: 2)]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("DECIMAL(10,2)", sql);
    }

    [Fact]
    public void Given_DecimalNoPrecision_When_BuildCreateTable_Then_DecimalDefault()
    {
        var table = TableWith("T", [Col("Price", "DECIMAL")]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("DECIMAL(18,2)", sql);
    }

    // ── ADD uses ADD not ADD COLUMN ────────────────────────────────────────

    [Fact]
    public void Given_NewColumn_When_BuildAddColumn_Then_UsesAddWithoutColumnKeyword()
    {
        var sql = Schema().BuildAddColumnSql("Orders", null, Col("Total", "DECIMAL"));
        Assert.Contains(" ADD ", sql);
        Assert.DoesNotContain("ADD COLUMN", sql);
    }

    // ── ALTER COLUMN ───────────────────────────────────────────────────────

    [Fact]
    public void Given_ColumnChange_When_BuildAlterColumn_Then_AlterColumnClause()
    {
        var sql = Schema().BuildAlterColumnSql("Orders", null, Col("Total", "DECIMAL", nullable: false));
        Assert.Contains("ALTER COLUMN", sql);
        Assert.Contains("[Total]", sql);
        Assert.Contains("NOT NULL", sql);
    }

    [Fact]
    public void Given_NullableColumn_When_BuildAlterColumn_Then_NullClause()
    {
        var sql = Schema().BuildAlterColumnSql("Orders", "dbo", Col("Notes", "VARCHAR", nullable: true));
        Assert.Contains("NULL", sql);
        Assert.DoesNotContain("NOT NULL", sql);
    }

    // ── DROP INDEX ON <table> ──────────────────────────────────────────────

    [Fact]
    public void Given_IndexName_When_BuildDropIndex_Then_DropIndexOnTable()
    {
        var sql = Schema().BuildDropIndexSql("Orders", null, "idx_orders_date");
        Assert.StartsWith("DROP INDEX", sql);
        Assert.Contains("[idx_orders_date]", sql);
        Assert.Contains("ON [Orders]", sql);
    }

    [Fact]
    public void Given_SchemaAndTable_When_BuildDropIndex_Then_SchemaQualified()
    {
        var sql = Schema().BuildDropIndexSql("Orders", "dbo", "idx_x");
        Assert.Contains("ON [dbo].[Orders]", sql);
    }

    // ── DROP CONSTRAINT ────────────────────────────────────────────────────

    [Fact]
    public void Given_ForeignKeyName_When_BuildDropForeignKey_Then_DropConstraint()
    {
        var sql = Schema().BuildDropForeignKeySql("OrderLines", null, "fk_orderlines_order");
        Assert.Contains("DROP CONSTRAINT", sql);
        Assert.Contains("[fk_orderlines_order]", sql);
        Assert.Contains("[OrderLines]", sql);
    }

    // ── CREATE INDEX ───────────────────────────────────────────────────────

    [Fact]
    public void Given_UniqueIndex_When_BuildCreateIndex_Then_ContainsUnique()
    {
        var sql = Schema().BuildCreateIndexSql("Users", null, new SchemaIndex("idx_u_email", ["Email"], true));
        Assert.Contains("CREATE UNIQUE INDEX", sql);
        Assert.Contains("[idx_u_email]", sql);
        Assert.Contains("[Users]", sql);
    }

    [Fact]
    public void Given_NonUniqueIndex_When_BuildCreateIndex_Then_NoUniqueKeyword()
    {
        var sql = Schema().BuildCreateIndexSql("Users", null, new SchemaIndex("idx_u_name", ["Name"], false));
        Assert.Contains("CREATE INDEX", sql);
        Assert.DoesNotContain("UNIQUE", sql);
    }

    // ── ADD FOREIGN KEY ────────────────────────────────────────────────────

    [Fact]
    public void Given_ForeignKey_When_BuildAddForeignKey_Then_ReferencesClause()
    {
        var fk = new SchemaForeignKey("fk_ol_order", ["OrderId"], "Orders", null, ["Id"],
            ReferentialRuleType.Cascade, null);

        var sql = Schema().BuildAddForeignKeySql("OrderLines", null, fk);

        Assert.Contains("FOREIGN KEY", sql);
        Assert.Contains("REFERENCES [Orders]", sql);
        Assert.Contains("ON DELETE CASCADE", sql);
        Assert.Contains("[fk_ol_order]", sql);
    }

    // ── NOT NULL in CREATE TABLE ───────────────────────────────────────────

    [Fact]
    public void Given_NotNullableColumn_When_BuildCreateTable_Then_NotNullPresent()
    {
        var table = TableWith("T", [Col("Code", "VARCHAR", nullable: false)]);
        var sql = Schema().BuildCreateTableSql(table);
        Assert.Contains("NOT NULL", sql);
    }
}
