using System.Linq;
using PersistNet.DbAbstraction;
using Xunit;

namespace PersistNet.Tests;

public class SchemaDifferTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static SchemaColumn Col(string name, string dbType, bool nullable = true,
        bool autoIncrement = false, int? size = null, int? precision = null, int? scale = null)
        => new(name, dbType, nullable, autoIncrement, null, size, precision, scale);

    private static SchemaIndex Idx(string name, bool unique = false, params string[] cols)
        => new(name, cols, unique);

    private static SchemaForeignKey Fk(string name, string fromCol, string toTable, string toCol)
        => new(name, [fromCol], toTable, null, [toCol], null, null);

    private static SchemaTable Table(string name, string? schema = null,
        SchemaColumn[]? cols = null,
        SchemaIndex[]? indexes = null,
        SchemaForeignKey[]? fks = null,
        SchemaPrimaryKey? pk = null)
        => new(name, schema, pk, cols ?? [], indexes ?? [], fks ?? []);

    private static SchemaSnapshot Snap(params SchemaTable[] tables)
        => new(tables);

    private static SchemaDiff Diff(SchemaSnapshot desired, SchemaSnapshot actual)
        => SchemaDiffer.Compute(desired, actual);

    // ── Empty / identical snapshots ────────────────────────────────────────

    [Fact]
    public void Given_BothEmpty_When_Compute_Then_DiffIsEmpty()
    {
        var diff = Diff(Snap(), Snap());
        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Given_IdenticalSingleTable_When_Compute_Then_DiffIsEmpty()
    {
        var table = Table("Orders", cols: [Col("Id", "INTEGER", nullable: false), Col("Total", "DECIMAL")]);
        var diff  = Diff(Snap(table), Snap(table));
        Assert.True(diff.IsEmpty);
    }

    // ── TablesToCreate ─────────────────────────────────────────────────────

    [Fact]
    public void Given_DesiredTableMissingFromActual_When_Compute_Then_TableIsInTablesToCreate()
    {
        var desired = Snap(Table("Products"));
        var actual  = Snap();

        var diff = Diff(desired, actual);

        Assert.Single(diff.TablesToCreate);
        Assert.Equal("Products", diff.TablesToCreate[0].Name);
    }

    [Fact]
    public void Given_NewTable_When_Compute_Then_ColumnsNotInColumnsToAdd()
    {
        // Columns of a brand-new table go into TablesToCreate, not ColumnsToAdd
        var table   = Table("Products", cols: [Col("Id", "INTEGER"), Col("Name", "VARCHAR")]);
        var diff    = Diff(Snap(table), Snap());

        Assert.Single(diff.TablesToCreate);
        Assert.Empty(diff.ColumnsToAdd);
    }

    // ── TablesToDrop ───────────────────────────────────────────────────────

    [Fact]
    public void Given_ActualTableAbsentFromDesired_When_Compute_Then_TableIsInTablesToDrop()
    {
        var actual  = Snap(Table("Orphan"));
        var desired = Snap();

        var diff = Diff(desired, actual);

        Assert.Single(diff.TablesToDrop);
        Assert.Equal("Orphan", diff.TablesToDrop[0].Name);
    }

    [Fact]
    public void Given_DroppedTable_When_Compute_Then_ColumnsNotInColumnsToDrop()
    {
        // Columns of a dropped table go into TablesToDrop, not ColumnsToDrop
        var table = Table("Orphan", cols: [Col("Id", "INTEGER")]);
        var diff  = Diff(Snap(), Snap(table));

        Assert.Single(diff.TablesToDrop);
        Assert.Empty(diff.ColumnsToDrop);
    }

    // ── ColumnsToAdd ───────────────────────────────────────────────────────

    [Fact]
    public void Given_NewColumnInDesired_When_Compute_Then_ColumnIsInColumnsToAdd()
    {
        var base_   = Table("T", cols: [Col("Id", "INTEGER")]);
        var desired = Table("T", cols: [Col("Id", "INTEGER"), Col("Name", "VARCHAR")]);

        var diff = Diff(Snap(desired), Snap(base_));

        Assert.Single(diff.ColumnsToAdd);
        Assert.Equal("Name", diff.ColumnsToAdd[0].Column.Name);
        Assert.Equal("T", diff.ColumnsToAdd[0].TableName);
    }

    // ── ColumnsToDrop ──────────────────────────────────────────────────────

    [Fact]
    public void Given_ColumnRemovedFromDesired_When_Compute_Then_ColumnIsInColumnsToDrop()
    {
        var desired = Table("T", cols: [Col("Id", "INTEGER")]);
        var actual  = Table("T", cols: [Col("Id", "INTEGER"), Col("OldField", "VARCHAR")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToDrop);
        Assert.Equal("OldField", diff.ColumnsToDrop[0].ColumnName);
    }

    // ── ColumnsToAlter — each ColumnsMatch field ───────────────────────────

    [Fact]
    public void Given_ColumnDbTypeChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Amount", "BIGINT")]);
        var actual  = Table("T", cols: [Col("Amount", "INTEGER")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
        Assert.Equal("Amount", diff.ColumnsToAlter[0].Column.Name);
        Assert.Equal("BIGINT", diff.ColumnsToAlter[0].Column.DbType);
    }

    [Fact]
    public void Given_DbTypeMatchIsCaseInsensitive_When_Compute_Then_NoDiff()
    {
        var desired = Table("T", cols: [Col("C", "varchar")]);
        var actual  = Table("T", cols: [Col("C", "VARCHAR")]);

        Assert.True(Diff(Snap(desired), Snap(actual)).IsEmpty);
    }

    [Fact]
    public void Given_ColumnNullabilityChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Name", "VARCHAR", nullable: false)]);
        var actual  = Table("T", cols: [Col("Name", "VARCHAR", nullable: true)]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
    }

    [Fact]
    public void Given_ColumnAutoIncrementChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Id", "INTEGER", autoIncrement: true)]);
        var actual  = Table("T", cols: [Col("Id", "INTEGER", autoIncrement: false)]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
    }

    [Fact]
    public void Given_ColumnSizeChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Name", "VARCHAR", size: 200)]);
        var actual  = Table("T", cols: [Col("Name", "VARCHAR", size: 100)]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
    }

    [Fact]
    public void Given_ColumnPrecisionChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Price", "DECIMAL", precision: 18)]);
        var actual  = Table("T", cols: [Col("Price", "DECIMAL", precision: 10)]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
    }

    [Fact]
    public void Given_ColumnScaleChanged_When_Compute_Then_ColumnIsInColumnsToAlter()
    {
        var desired = Table("T", cols: [Col("Price", "DECIMAL", precision: 10, scale: 4)]);
        var actual  = Table("T", cols: [Col("Price", "DECIMAL", precision: 10, scale: 2)]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAlter);
    }

    [Fact]
    public void Given_ColumnNamesAreCaseInsensitive_When_Compute_Then_ColumnsMatched()
    {
        var desired = Table("T", cols: [Col("myCol", "INTEGER")]);
        var actual  = Table("T", cols: [Col("MYCOL", "INTEGER")]);

        Assert.True(Diff(Snap(desired), Snap(actual)).IsEmpty);
    }

    // ── IndexesToCreate / IndexesToDrop ────────────────────────────────────

    [Fact]
    public void Given_NewNamedIndex_When_Compute_Then_IndexIsInIndexesToCreate()
    {
        var desired = Table("T", cols: [Col("C", "INTEGER")], indexes: [Idx("idx_c", cols: "C")]);
        var actual  = Table("T", cols: [Col("C", "INTEGER")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.IndexesToCreate);
        Assert.Equal("idx_c", diff.IndexesToCreate[0].Index.Name);
    }

    [Fact]
    public void Given_RemovedNamedIndex_When_Compute_Then_IndexIsInIndexesToDrop()
    {
        var desired = Table("T", cols: [Col("C", "INTEGER")]);
        var actual  = Table("T", cols: [Col("C", "INTEGER")], indexes: [Idx("idx_c", cols: "C")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.IndexesToDrop);
        Assert.Equal("idx_c", diff.IndexesToDrop[0].IndexName);
    }

    [Fact]
    public void Given_UnnamedIndex_When_Compute_Then_IgnoredInDiff()
    {
        // Unnamed indexes (name = null) cannot be tracked by name — they are ignored
        var unnamedIdx = new SchemaIndex(null, ["C"], false);
        var desired    = Table("T", cols: [Col("C", "INTEGER")]);
        var actual     = Table("T", cols: [Col("C", "INTEGER")], indexes: [unnamedIdx]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Empty(diff.IndexesToDrop);
    }

    // ── ForeignKeysToAdd / ForeignKeysToDrop ───────────────────────────────

    [Fact]
    public void Given_NewNamedForeignKey_When_Compute_Then_FKIsInForeignKeysToAdd()
    {
        var desired = Table("Orders",
            cols:  [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")],
            fks:   [Fk("fk_orders_customer", "CustomerId", "Customers", "Id")]);
        var actual = Table("Orders",
            cols: [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ForeignKeysToAdd);
        Assert.Equal("fk_orders_customer", diff.ForeignKeysToAdd[0].ForeignKey.Name);
    }

    [Fact]
    public void Given_RemovedNamedForeignKey_When_Compute_Then_FKIsInForeignKeysToDrop()
    {
        var desired = Table("Orders", cols: [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")]);
        var actual  = Table("Orders",
            cols: [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")],
            fks:  [Fk("fk_orders_customer", "CustomerId", "Customers", "Id")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ForeignKeysToDrop);
        Assert.Equal("fk_orders_customer", diff.ForeignKeysToDrop[0].ForeignKeyName);
    }

    [Fact]
    public void Given_UnnamedForeignKey_When_Compute_Then_IgnoredInDiff()
    {
        var unnamedFk = new SchemaForeignKey(null, ["CustomerId"], "Customers", null, ["Id"], null, null);
        var desired   = Table("Orders", cols: [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")]);
        var actual    = Table("Orders",
            cols: [Col("Id", "INTEGER"), Col("CustomerId", "INTEGER")],
            fks:  [unnamedFk]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Empty(diff.ForeignKeysToDrop);
    }

    // ── Table key / schema matching ────────────────────────────────────────

    [Fact]
    public void Given_TableNamesAreCaseInsensitive_When_Compute_Then_TablesMatched()
    {
        var desired = Snap(Table("products"));
        var actual  = Snap(Table("PRODUCTS"));

        Assert.True(Diff(desired, actual).IsEmpty);
    }

    [Fact]
    public void Given_SameNameDifferentSchema_When_Compute_Then_TreatedAsDistinctTables()
    {
        var desired = Snap(Table("Orders", schema: "sales"));
        var actual  = Snap(Table("Orders", schema: "archive"));

        var diff = Diff(desired, actual);

        Assert.Single(diff.TablesToCreate);
        Assert.Equal("sales", diff.TablesToCreate[0].Schema);
        Assert.Single(diff.TablesToDrop);
        Assert.Equal("archive", diff.TablesToDrop[0].Schema);
    }

    [Fact]
    public void Given_SchemaMatchIsCaseInsensitive_When_Compute_Then_TablesMatched()
    {
        var desired = Snap(Table("Orders", schema: "dbo"));
        var actual  = Snap(Table("Orders", schema: "DBO"));

        Assert.True(Diff(desired, actual).IsEmpty);
    }

    // ── TableSchema propagation ────────────────────────────────────────────

    [Fact]
    public void Given_TableWithSchema_When_ColumnAdded_Then_TableSchemaPopulatedInDiff()
    {
        var desired = Table("Orders", schema: "dbo", cols: [Col("Id", "INTEGER"), Col("Note", "VARCHAR")]);
        var actual  = Table("Orders", schema: "dbo", cols: [Col("Id", "INTEGER")]);

        var diff = Diff(Snap(desired), Snap(actual));

        Assert.Single(diff.ColumnsToAdd);
        Assert.Equal("dbo", diff.ColumnsToAdd[0].TableSchema);
    }

    // ── Multiple tables — changes isolated ────────────────────────────────

    [Fact]
    public void Given_MultipleTables_When_OneModified_Then_OnlyThatTableDiffed()
    {
        var unchanged = Table("Customers", cols: [Col("Id", "INTEGER")]);
        var changed   = Table("Orders",
            cols: [Col("Id", "INTEGER"), Col("Total", "DECIMAL")]);
        var changedActual = Table("Orders",
            cols: [Col("Id", "INTEGER")]);

        var diff = Diff(
            Snap(unchanged, changed),
            Snap(unchanged, changedActual));

        Assert.Empty(diff.TablesToCreate);
        Assert.Empty(diff.TablesToDrop);
        Assert.Single(diff.ColumnsToAdd);
        Assert.Equal("Orders", diff.ColumnsToAdd[0].TableName);
    }
}
