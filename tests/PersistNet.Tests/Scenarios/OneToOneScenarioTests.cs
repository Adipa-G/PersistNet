using System;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: One-to-One relationship.
///
/// Schema:
///   sc_oto_employees (Id, Name)
///   sc_oto_profiles  (Id, EmployeeId FK→employees.Id, Bio)
///
/// Ownership: OtoProfile holds the FK (owning side).
///            OtoEmployee.Profile is the inverse side (MappedBy).
/// </summary>
public sealed class OneToOneScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_oto_employees")]
    private class OtoEmployee
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        /// <summary>Inverse side — OtoProfile owns the FK.</summary>
        [OneToOneRelationshipInfo(RelatedType = typeof(OtoProfile), MappedBy = "Employee")]
        public OtoProfile? Profile { get; set; }
    }

    [TableInfo(TableName = "sc_oto_profiles")]
    private class OtoProfile
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int EmployeeId { get; set; }

        [ColumnInfo]
        public string Bio { get; set; } = "";

        /// <summary>Owning side — holds the EmployeeId FK.</summary>
        [OneToOneRelationshipInfo(
            RelatedType = typeof(OtoEmployee),
            FromKeys = new[] { "EmployeeId" },
            ToKeys = new[] { "Id" })]
        public OtoEmployee? Employee { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_oto_employees " +
            "(Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE sc_oto_profiles " +
            "(Id INTEGER NOT NULL PRIMARY KEY, EmployeeId INTEGER NOT NULL UNIQUE, Bio TEXT NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save from the inverse side (Employee → Profile).
    /// Expected save order: employee first, then profile (profile is saved as O2O inverse child).
    /// </summary>
    [Fact]
    public async Task Save_FromInverseSide_EmployeeAndProfileBothPersisted()
    {
        await CreateTablesAsync();

        var employee = new OtoEmployee { Name = "Alice" };
        var profile  = new OtoProfile  { EmployeeId = 1, Bio = "Engineer" }; // FK = 1 (first auto-increment)
        employee.Profile = profile;

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(employee);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_oto_employees"));
        Assert.Equal(1L, await CountAsync("sc_oto_profiles"));

        Assert.Equal("Alice",    await ScalarAsync("SELECT Name       FROM sc_oto_employees WHERE Id = 1"));
        Assert.Equal("Engineer", await ScalarAsync("SELECT Bio        FROM sc_oto_profiles  WHERE Id = 1"));
        Assert.Equal(1L,         Convert.ToInt64(await ScalarAsync("SELECT EmployeeId FROM sc_oto_profiles WHERE Id = 1")));
    }

    /// <summary>
    /// Save from the owning side (Profile → Employee).
    /// Expected save order: employee first (FK dependency), then profile.
    /// </summary>
    [Fact]
    public async Task Save_FromOwningSide_EmployeeSavedBeforeProfile()
    {
        await CreateTablesAsync();

        var employee = new OtoEmployee { Name = "Bob" };
        var profile  = new OtoProfile  { EmployeeId = 1, Bio = "Manager", Employee = employee }; // FK = 1 (first auto-increment)

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(profile);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_oto_employees"));
        Assert.Equal(1L, await CountAsync("sc_oto_profiles"));

        Assert.Equal("Bob",     await ScalarAsync("SELECT Name FROM sc_oto_employees LIMIT 1"));
        Assert.Equal("Manager", await ScalarAsync("SELECT Bio  FROM sc_oto_profiles  LIMIT 1"));
    }

    /// <summary>
    /// Update the employee name via GetAsync + Save.
    /// Dirty tracking must not touch the profile row.
    /// </summary>
    [Fact]
    public async Task GetAsync_Employee_ThenUpdateName_ProfileRowUntouched()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_oto_employees VALUES (1, 'Charlie')");
        await ExecAsync("INSERT INTO sc_oto_profiles  VALUES (1, 1, 'Analyst')");

        await using var txn = await Factory.OpenTransactionAsync();
        var employee = await txn.GetAsync<OtoEmployee>(1);
        employee.Name = "Charlie-Updated";
        txn.Save(employee);
        await txn.CommitAsync();

        Assert.Equal("Charlie-Updated",
            await ScalarAsync("SELECT Name FROM sc_oto_employees WHERE Id = 1"));
        // Profile row untouched (not loaded, not saved).
        Assert.Equal("Analyst",
            await ScalarAsync("SELECT Bio FROM sc_oto_profiles WHERE Id = 1"));
    }
}
