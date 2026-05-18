using System;
using System.Collections.Generic;
using System.Linq;
using PersistNet.DbAbstraction;
using PersistNet.DbInfo;
using PersistNet.Entities;
using Xunit;

namespace PersistNet.Tests;

/// <summary>
/// Unit tests for SQL generation in <see cref="AnsiSqlPersistenceBase"/> and
/// <see cref="SqlServerPersistence"/>.  No database connection is required —
/// the <c>protected internal Build*Sql</c> methods are tested directly via
/// thin concrete subclasses that pass <c>null</c> for the connection.
/// </summary>
public class AnsiSqlPersistenceTests
{
    // ── Thin concrete classes for testing (no real DB connection needed) ────

    private sealed class AnsiPersistence : AnsiSqlPersistenceBase
    {
        internal AnsiPersistence() : base(null!) { }
    }

    private static SqlServerPersistence SqlServer() => new(null!);

    // ── Fixture entity types (registered into DbInfoCache on first use) ─────

    [TableInfo(TableName = "items")]
    private class Item
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Label { get; set; } = "";

        [ColumnInfo]
        public decimal Cost { get; set; }
    }

    [TableInfo(TableName = "line_items")]
    private class LineItem
    {
        [ColumnInfo(Key = true)]
        public int OrderId { get; set; }

        [ColumnInfo(Key = true)]
        public int ItemId { get; set; }

        [ColumnInfo]
        public int Qty { get; set; }
    }

    private static void WarmCache() => DbInfoCache.GetOrExtract(typeof(Item).Assembly);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static MultiRowInsert MakeInsert(string table, IReadOnlyList<string> cols,
        params IReadOnlyList<object?>[] rows) =>
        new(table, null, cols, rows);

    private static GroupedUpdate MakeUpdate(string table,
        IReadOnlyList<SetClause> set,
        IReadOnlyList<string> keyCols,
        params IReadOnlyList<object?>[] keyValues) =>
        new(table, null, set, keyCols, keyValues);

    private static BatchDelete MakeDelete(string table,
        IReadOnlyList<string> keyCols,
        params IReadOnlyList<object?>[] keyValues) =>
        new(table, null, keyCols, keyValues);

    // ── INSERT ───────────────────────────────────────────────────────────────

    [Fact]
    public void Given_SingleRowInsert_When_BuildInsertSql_Then_GeneratesCorrectSql()
    {
        var ansi = new AnsiPersistence();
        var insert = MakeInsert("items", new[] { "Id", "Label", "Cost" },
            new object?[] { 0, "Widget", 9.99m });

        var (sql, parameters) = ansi.BuildInsertSql(insert);

        Assert.Equal(
            "INSERT INTO \"items\" (\"Id\", \"Label\", \"Cost\") VALUES (@p0, @p1, @p2)",
            sql);
        Assert.Equal(3, parameters.Count);
        Assert.Equal(("@p0", (object?)0), parameters[0]);
        Assert.Equal(("@p1", (object?)"Widget"), parameters[1]);
        Assert.Equal(("@p2", (object?)9.99m), parameters[2]);
    }

    [Fact]
    public void Given_MultipleRowInsert_When_BuildInsertSql_Then_ParameterCountIsRowsTimesColumns()
    {
        var ansi = new AnsiPersistence();
        var insert = MakeInsert("items", new[] { "Id", "Label", "Cost" },
            new object?[] { 0, "A", 1m },
            new object?[] { 0, "B", 2m },
            new object?[] { 0, "C", 3m });

        var (_, parameters) = ansi.BuildInsertSql(insert);

        Assert.Equal(9, parameters.Count); // 3 rows × 3 columns
    }

    [Fact]
    public void Given_MultipleRowInsert_When_BuildInsertSql_Then_ValuesClauseHasCorrectTupleCount()
    {
        var ansi = new AnsiPersistence();
        var insert = MakeInsert("items", new[] { "Id", "Label", "Cost" },
            new object?[] { 0, "A", 1m },
            new object?[] { 0, "B", 2m });

        var (sql, _) = ansi.BuildInsertSql(insert);

        // Two value tuples separated by ", "
        var valuesPart = sql.Substring(sql.IndexOf("VALUES") + 6).Trim();
        Assert.Equal(2, valuesPart.Split("), (").Length);
    }

    // ── SQL Server OUTPUT INSERTED ────────────────────────────────────────────

    private static MultiRowInsert MakeInsertWithKey(string table, string keyCol,
        IReadOnlyList<string> cols, params IReadOnlyList<object?>[] rows) =>
        new(table, null, cols, rows, KeyCallbacks: null, AutoIncrKeyColumn: keyCol);

    [Fact]
    public void Given_SingleRowInsert_When_BuildInsertWithOutputSql_Then_ContainsOutputClause()
    {
        var ss     = SqlServer();
        var insert = MakeInsertWithKey("orders", "OrderId", new[] { "Name", "OrderDate" },
            new object?[] { "Acme", new DateTime(2026, 1, 1) });

        var (sql, parameters) = ss.BuildInsertWithOutputSql(insert, insert.ValueRows);

        Assert.Contains("OUTPUT INSERTED.[OrderId]", sql);
        Assert.StartsWith("INSERT INTO [orders] ([Name], [OrderDate]) OUTPUT INSERTED.[OrderId]", sql);
        Assert.Contains("VALUES (@p0, @p1)", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void Given_MultipleRowInsert_When_BuildInsertWithOutputSql_Then_SingleValuesClause()
    {
        var ss     = SqlServer();
        var insert = MakeInsertWithKey("orders", "OrderId", new[] { "Name", "OrderDate" },
            new object?[] { "Acme", new DateTime(2026, 1, 1) },
            new object?[] { "Beta", new DateTime(2026, 2, 1) },
            new object?[] { "Ceta", new DateTime(2026, 3, 1) });

        var (sql, parameters) = ss.BuildInsertWithOutputSql(insert, insert.ValueRows);

        Assert.Contains("OUTPUT INSERTED.[OrderId]", sql);
        // Three rows → three parameter tuples in the VALUES list.
        var valuesPart = sql[(sql.IndexOf("VALUES", StringComparison.Ordinal) + 6)..].Trim();
        Assert.Equal(3, valuesPart.Split("), (").Length);
        Assert.Equal(6, parameters.Count);
        // Parameters are named @p0 through @p5 continuously.
        Assert.Equal("@p0", parameters[0].Name);
        Assert.Equal("@p5", parameters[5].Name);
    }

    [Fact]
    public void Given_SchemaQualifiedTable_When_BuildInsertWithOutputSql_Then_SchemaIsQuoted()
    {
        var ss     = SqlServer();
        var insert = new MultiRowInsert("orders", "dbo", new[] { "Name" },
            new[] { (IReadOnlyList<object?>)new object?[] { "Acme" } },
            AutoIncrKeyColumn: "OrderId");

        var (sql, _) = ss.BuildInsertWithOutputSql(insert, insert.ValueRows);

        Assert.StartsWith("INSERT INTO [dbo].[orders]", sql);
    }

    // ── UPDATE — single key ──────────────────────────────────────────────────

    [Fact]
    public void Given_SingleKeyUpdate_When_BuildUpdateSql_Then_UsesInClauseWithAnsiQuoting()
    {
        var ansi = new AnsiPersistence();
        var update = MakeUpdate("items",
            new[] { new SetClause("Label", "Widget"), new SetClause("Cost", 9.99m) },
            new[] { "Id" },
            new object?[] { 1 },
            new object?[] { 2 },
            new object?[] { 3 });

        var (sql, parameters) = ansi.BuildUpdateSql(update);

        Assert.StartsWith("UPDATE \"items\" SET", sql);
        Assert.Contains("\"Label\"=@p0", sql);
        Assert.Contains("\"Cost\"=@p1", sql);
        Assert.Contains("\"Id\" IN (@p2, @p3, @p4)", sql);
        Assert.Equal(5, parameters.Count); // 2 SET + 3 key
    }

    [Fact]
    public void Given_SingleKeyUpdate_When_BuildUpdateSql_Then_SetAndKeyParametersUseContiguousFlatIndex()
    {
        var ansi = new AnsiPersistence();
        var update = MakeUpdate("items",
            new[] { new SetClause("Label", "X") },
            new[] { "Id" },
            new object?[] { 10 });

        var (_, parameters) = ansi.BuildUpdateSql(update);

        Assert.Equal("@p0", parameters[0].Name); // SET
        Assert.Equal("@p1", parameters[1].Name); // key
    }

    // ── UPDATE — composite key ───────────────────────────────────────────────

    [Fact]
    public void Given_CompositeKeyUpdate_When_AnsiSqlBuildUpdateSql_Then_UsesRowValueConstructor()
    {
        var ansi = new AnsiPersistence();
        var update = MakeUpdate("line_items",
            new[] { new SetClause("Qty", 5) },
            new[] { "OrderId", "ItemId" },
            new object?[] { 1, 10 },
            new object?[] { 2, 20 });

        var (sql, _) = ansi.BuildUpdateSql(update);

        Assert.Contains("(\"OrderId\", \"ItemId\") IN", sql);
        Assert.DoesNotContain("OR", sql);
    }

    [Fact]
    public void Given_CompositeKeyUpdate_When_SqlServerBuildUpdateSql_Then_UsesOrChains()
    {
        var ss = SqlServer();
        var update = MakeUpdate("line_items",
            new[] { new SetClause("Qty", 5) },
            new[] { "OrderId", "ItemId" },
            new object?[] { 1, 10 },
            new object?[] { 2, 20 });

        var (sql, _) = ss.BuildUpdateSql(update);

        Assert.Contains("[OrderId]=@p1 AND [ItemId]=@p2", sql);
        Assert.Contains("[OrderId]=@p3 AND [ItemId]=@p4", sql);
        Assert.Contains(") OR (", sql);
        Assert.DoesNotContain("IN", sql.Substring(sql.IndexOf("WHERE")));
    }

    // ── DELETE — single key ──────────────────────────────────────────────────

    [Fact]
    public void Given_SingleKeyDelete_When_BuildDeleteSql_Then_UsesInClause()
    {
        var ansi = new AnsiPersistence();
        var delete = MakeDelete("items", new[] { "Id" },
            new object?[] { 1 },
            new object?[] { 2 });

        var (sql, parameters) = ansi.BuildDeleteSql(delete);

        Assert.Equal("DELETE FROM \"items\" WHERE \"Id\" IN (@p0, @p1)", sql);
        Assert.Equal(2, parameters.Count);
        Assert.Equal(1, parameters[0].Value);
        Assert.Equal(2, parameters[1].Value);
    }

    // ── DELETE — composite key ───────────────────────────────────────────────

    [Fact]
    public void Given_CompositeKeyDelete_When_AnsiSqlBuildDeleteSql_Then_UsesRowValueConstructor()
    {
        var ansi = new AnsiPersistence();
        var delete = MakeDelete("line_items", new[] { "OrderId", "ItemId" },
            new object?[] { 1, 10 },
            new object?[] { 2, 20 });

        var (sql, _) = ansi.BuildDeleteSql(delete);

        Assert.Contains("(\"OrderId\", \"ItemId\") IN", sql);
    }

    [Fact]
    public void Given_CompositeKeyDelete_When_SqlServerBuildDeleteSql_Then_UsesOrChains()
    {
        var ss = SqlServer();
        var delete = MakeDelete("line_items", new[] { "OrderId", "ItemId" },
            new object?[] { 1, 10 },
            new object?[] { 2, 20 });

        var (sql, _) = ss.BuildDeleteSql(delete);

        Assert.Contains("([OrderId]=@p0 AND [ItemId]=@p1) OR ([OrderId]=@p2 AND [ItemId]=@p3)", sql);
    }

    // ── SQL Server identifier quoting ────────────────────────────────────────

    [Fact]
    public void Given_SqlServerDialect_When_BuildInsertSql_Then_AllIdentifiersQuotedWithBrackets()
    {
        var ss = SqlServer();
        var insert = MakeInsert("items", new[] { "Id", "Label" },
            new object?[] { 1, "A" });

        var (sql, _) = ss.BuildInsertSql(insert);

        Assert.Contains("[items]", sql);
        Assert.Contains("[Id]", sql);
        Assert.Contains("[Label]", sql);
        Assert.DoesNotContain("\"", sql);
    }

    // ── Null values ──────────────────────────────────────────────────────────

    [Fact]
    public void Given_InsertWithNullCellValue_When_BuildInsertSql_Then_ParameterValueIsNull()
    {
        var ansi = new AnsiPersistence();
        var insert = MakeInsert("items", new[] { "Id", "Label", "Cost" },
            new object?[] { 0, null, 1m });

        var (_, parameters) = ansi.BuildInsertSql(insert);

        var labelParam = parameters.Single(p => p.Name == "@p1");
        Assert.Null(labelParam.Value);
    }

    [Fact]
    public void Given_UpdateWithNullSetValue_When_BuildUpdateSql_Then_ParameterValueIsNull()
    {
        var ansi = new AnsiPersistence();
        var update = MakeUpdate("items",
            new[] { new SetClause("Label", null) },
            new[] { "Id" },
            new object?[] { 1 });

        var (_, parameters) = ansi.BuildUpdateSql(update);

        var setParam = parameters.Single(p => p.Name == "@p0");
        Assert.Null(setParam.Value);
    }

    // ── Version column (optimistic concurrency) ──────────────────────────────

    private static GroupedUpdate MakeVersionedUpdate(string table,
        IReadOnlyList<SetClause> set,
        IReadOnlyList<string> keyCols,
        string versionColumn,
        object? expectedVersion,
        params IReadOnlyList<object?>[] keyValues) =>
        new(table, null, set, keyCols, keyValues, versionColumn, expectedVersion);

    [Fact]
    public void Given_UpdateWithVersionColumn_When_BuildUpdateSql_Then_WhereIncludesVersionPredicate()
    {
        var ansi = new AnsiPersistence();
        var update = MakeVersionedUpdate(
            "items",
            new[] { new SetClause("Label", "NewLabel"), new SetClause("Version", 4L) },
            new[] { "Id" },
            versionColumn: "Version",
            expectedVersion: 3L,
            new object?[] { 1 });

        var (sql, _) = ansi.BuildUpdateSql(update);

        // WHERE must contain both the key predicate and the version guard.
        Assert.Contains("\"Id\" IN", sql);
        Assert.Contains("AND \"Version\"=", sql);
    }

    [Fact]
    public void Given_UpdateWithVersionColumn_When_BuildUpdateSql_Then_VersionParamOrderIsAfterKeys()
    {
        var ansi = new AnsiPersistence();
        var update = MakeVersionedUpdate(
            "items",
            new[] { new SetClause("Label", "X"), new SetClause("Version", 2L) },
            new[] { "Id" },
            versionColumn: "Version",
            expectedVersion: 1L,
            new object?[] { 7 });

        var (_, parameters) = ansi.BuildUpdateSql(update);

        // Parameters: @p0=Label SET, @p1=Version SET, @p2=Id key, @p3=expected version
        Assert.Equal(4, parameters.Count);
        Assert.Equal("X",  parameters[0].Value); // SET Label
        Assert.Equal(2L,   parameters[1].Value); // SET Version (new)
        Assert.Equal(7,    parameters[2].Value); // WHERE Id
        Assert.Equal(1L,   parameters[3].Value); // WHERE Version = expected
    }
}
