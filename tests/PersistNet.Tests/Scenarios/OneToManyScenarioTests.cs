using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: One-to-Many and Many-to-One relationships.
///
/// Schema:
///   sc_o2m_depts   (Id, Name)
///   sc_o2m_members (Id, DeptId FK→depts.Id, Name)
///
/// Ownership: O2mMember holds the FK (many side).
///            O2mDept.Members is the one side (MappedBy).
/// </summary>
public sealed class OneToManyScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_o2m_depts")]
    private class O2mDept
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [OneToManyRelationshipInfo(RelatedType = typeof(O2mMember), MappedBy = "Dept")]
        public List<O2mMember>? Members { get; set; }
    }

    [TableInfo(TableName = "sc_o2m_members")]
    private class O2mMember
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int DeptId { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(O2mDept),
            FromKeys = new[] { "DeptId" },
            ToKeys = new[] { "Id" })]
        public O2mDept? Dept { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_o2m_depts " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE sc_o2m_members " +
            "(Id INTEGER NOT NULL PRIMARY KEY, DeptId INTEGER NOT NULL, Name TEXT NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One-to-Many: save a department with two members.
    /// Department must be persisted before members (FK dependency).
    /// </summary>
    [Fact]
    public async Task Save_DepartmentWithTwoMembers_AllThreeRowsPersisted()
    {
        await CreateTablesAsync();

        var dept = new O2mDept { Name = "Engineering" };
        var m1   = new O2mMember { DeptId = 1, Name = "Alice" }; // DeptId = 1: first auto-increment
        var m2   = new O2mMember { DeptId = 1, Name = "Bob" };
        dept.Members = new List<O2mMember> { m1, m2 };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(dept);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_o2m_depts"));
        Assert.Equal(2L, await CountAsync("sc_o2m_members"));

        Assert.Equal("Engineering",
            await ScalarAsync("SELECT Name FROM sc_o2m_depts LIMIT 1"));
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sc_o2m_members WHERE DeptId = 1")));
    }

    /// <summary>
    /// Many-to-One: save a member with its department reference set.
    /// Department is saved first because the member carries the FK.
    /// </summary>
    [Fact]
    public async Task Save_MemberWithDeptRef_DeptAndMemberBothPersisted()
    {
        await CreateTablesAsync();

        var dept   = new O2mDept   { Name = "HR" };
        var member = new O2mMember { DeptId = 1, Name = "Carol", Dept = dept }; // DeptId = 1: first auto-increment

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(member);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_o2m_depts"));
        Assert.Equal(1L, await CountAsync("sc_o2m_members"));

        Assert.Equal("HR",    await ScalarAsync("SELECT Name FROM sc_o2m_depts   LIMIT 1"));
        Assert.Equal("Carol", await ScalarAsync("SELECT Name FROM sc_o2m_members LIMIT 1"));
        Assert.Equal(1L, Convert.ToInt64(
            await ScalarAsync("SELECT DeptId FROM sc_o2m_members LIMIT 1")));
    }

    /// <summary>
    /// Update only the department name.
    /// Member rows must be completely unaffected (dirty tracking + no cascade).
    /// </summary>
    [Fact]
    public async Task GetAsync_Department_ThenUpdateName_MembersUnaffected()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_o2m_depts   VALUES (1, 'Finance')");
        await ExecAsync("INSERT INTO sc_o2m_members VALUES (1, 1, 'Dave')");
        await ExecAsync("INSERT INTO sc_o2m_members VALUES (2, 1, 'Eve')");

        await using var txn = await Factory.OpenTransactionAsync();
        var dept = await txn.GetAsync<O2mDept>(1);
        dept.Name = "Finance-Renamed";
        txn.Save(dept); // no Members loaded → only dept row updated
        await txn.CommitAsync();

        Assert.Equal("Finance-Renamed",
            await ScalarAsync("SELECT Name FROM sc_o2m_depts WHERE Id = 1"));
        // Members untouched.
        Assert.Equal("Dave",
            await ScalarAsync("SELECT Name FROM sc_o2m_members WHERE Id = 1"));
        Assert.Equal("Eve",
            await ScalarAsync("SELECT Name FROM sc_o2m_members WHERE Id = 2"));
    }

    /// <summary>
    /// Delete a department cascade: members are deleted first, then the department.
    /// The Members navigation must be populated for cascade delete to traverse correctly.
    /// </summary>
    [Fact]
    public async Task Delete_DepartmentWithMembers_MembersDeletedFirst()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_o2m_depts   VALUES (1, 'Legal')");
        await ExecAsync("INSERT INTO sc_o2m_members VALUES (1, 1, 'Frank')");
        await ExecAsync("INSERT INTO sc_o2m_members VALUES (2, 1, 'Grace')");

        // Build entity graph for cascade delete (navigation properties must be populated).
        var m1   = new O2mMember { Id = 1, DeptId = 1 };
        var m2   = new O2mMember { Id = 2, DeptId = 1 };
        var dept = new O2mDept   { Id = 1, Members = new List<O2mMember> { m1, m2 } };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Delete(dept);
        await txn.CommitAsync();

        Assert.Equal(0L, await CountAsync("sc_o2m_members"));
        Assert.Equal(0L, await CountAsync("sc_o2m_depts"));
    }
}
