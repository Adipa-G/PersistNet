using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Eager loading of related entities via GetAsync().Include() and IncludeAll().
///
/// Schema:
///   el_depts     (Id PK AUTOINCREMENT, Name)
///   el_members   (Id PK AUTOINCREMENT, DeptId FK, Name)
///   el_actors    (Id PK AUTOINCREMENT, Name)
///   el_movies    (Id PK AUTOINCREMENT, Title)
///   el_castings  (ActorId FK, MovieId FK, PK composite)
///   el_employees (Id PK AUTOINCREMENT, Name)
///   el_profiles  (Id PK AUTOINCREMENT, EmployeeId FK UNIQUE, Bio)
/// </summary>
public sealed class EagerLoadingScenarioTests : ScenarioTestBase
{
    // ── Entity models ────────────────────────────────────────────────────────

    [TableInfo(TableName = "el_depts")]
    private class ElDept
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [OneToManyRelationshipInfo(RelatedType = typeof(ElMember), MappedBy = "Dept")]
        public List<ElMember>? Members { get; set; }
    }

    [TableInfo(TableName = "el_members")]
    private class ElMember
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int DeptId { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(ElDept),
            FromKeys = new[] { "DeptId" },
            ToKeys = new[] { "Id" })]
        public ElDept? Dept { get; set; }
    }

    [TableInfo(TableName = "el_actors")]
    private class ElActor
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ManyToManyRelationshipInfo(
            RelatedType = typeof(ElMovie),
            JoinTableName = "el_castings",
            LeftKeyColumns = new[] { "ActorId" },
            RightKeyColumns = new[] { "MovieId" },
            LeftForeignKeys = new[] { "Id" },
            RightForeignKeys = new[] { "Id" })]
        public List<ElMovie>? Movies { get; set; }
    }

    [TableInfo(TableName = "el_movies")]
    private class ElMovie
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Title { get; set; } = "";

        [ManyToManyRelationshipInfo(RelatedType = typeof(ElActor), MappedBy = "Movies")]
        public List<ElActor>? Actors { get; set; }
    }

    [TableInfo(TableName = "el_employees")]
    private class ElEmployee
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [OneToOneRelationshipInfo(RelatedType = typeof(ElProfile), MappedBy = "Employee")]
        public ElProfile? Profile { get; set; }
    }

    [TableInfo(TableName = "el_profiles")]
    private class ElProfile
    {
        [ColumnInfo(Key = true, AutoIncrement = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int EmployeeId { get; set; }

        [ColumnInfo]
        public string Bio { get; set; } = "";

        [OneToOneRelationshipInfo(
            RelatedType = typeof(ElEmployee),
            FromKeys = new[] { "EmployeeId" },
            ToKeys = new[] { "Id" })]
        public ElEmployee? Employee { get; set; }
    }

    // ── DDL helpers ──────────────────────────────────────────────────────────

    private async Task CreateDeptMemberTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE el_depts (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_members " +
            "(Id INTEGER NOT NULL PRIMARY KEY, DeptId INTEGER NOT NULL, Name TEXT NOT NULL)");
    }

    private async Task CreateActorMovieTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE el_actors (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_movies (Id INTEGER NOT NULL PRIMARY KEY, Title TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_castings " +
            "(ActorId INTEGER NOT NULL, MovieId INTEGER NOT NULL, PRIMARY KEY (ActorId, MovieId))");
    }

    private async Task CreateEmployeeProfileTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE el_employees (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_profiles " +
            "(Id INTEGER NOT NULL PRIMARY KEY, EmployeeId INTEGER NOT NULL UNIQUE, Bio TEXT NOT NULL)");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>O2M — GetAsync with Include loads the collection of children.</summary>
    [Fact]
    public async Task GetAsync_Include_O2M_LoadsMembers()
    {
        await CreateDeptMemberTablesAsync();
        await ExecAsync("INSERT INTO el_depts VALUES (1, 'Engineering')");
        await ExecAsync("INSERT INTO el_members VALUES (1, 1, 'Alice')");
        await ExecAsync("INSERT INTO el_members VALUES (2, 1, 'Bob')");

        await using var txn = await Factory.OpenTransactionAsync();
        var dept = await txn.GetAsync<ElDept>(1)
            .Include(d => d.Members);

        Assert.NotNull(dept.Members);
        Assert.Equal(2, dept.Members!.Count);
        Assert.Contains(dept.Members, m => m.Name == "Alice");
        Assert.Contains(dept.Members, m => m.Name == "Bob");
    }

    /// <summary>M2O — GetAsync with Include loads the parent reference.</summary>
    [Fact]
    public async Task GetAsync_Include_M2O_LoadsDept()
    {
        await CreateDeptMemberTablesAsync();
        await ExecAsync("INSERT INTO el_depts VALUES (1, 'Engineering')");
        await ExecAsync("INSERT INTO el_members VALUES (1, 1, 'Alice')");

        await using var txn = await Factory.OpenTransactionAsync();
        var member = await txn.GetAsync<ElMember>(1)
            .Include(m => m.Dept);

        Assert.NotNull(member.Dept);
        Assert.Equal("Engineering", member.Dept!.Name);
    }

    /// <summary>M2M (owning side) — GetAsync with Include loads the joined collection.</summary>
    [Fact]
    public async Task GetAsync_Include_M2M_LoadsMovies()
    {
        await CreateActorMovieTablesAsync();
        await ExecAsync("INSERT INTO el_actors VALUES (1, 'Tom Hanks')");
        await ExecAsync("INSERT INTO el_movies VALUES (1, 'Cast Away')");
        await ExecAsync("INSERT INTO el_movies VALUES (2, 'Forrest Gump')");
        await ExecAsync("INSERT INTO el_castings VALUES (1, 1)");
        await ExecAsync("INSERT INTO el_castings VALUES (1, 2)");

        await using var txn = await Factory.OpenTransactionAsync();
        var actor = await txn.GetAsync<ElActor>(1)
            .Include(a => a.Movies);

        Assert.NotNull(actor.Movies);
        Assert.Equal(2, actor.Movies!.Count);
        Assert.Contains(actor.Movies, m => m.Title == "Cast Away");
        Assert.Contains(actor.Movies, m => m.Title == "Forrest Gump");
    }

    /// <summary>O2O (inverse side) — GetAsync with Include loads the owned profile.</summary>
    [Fact]
    public async Task GetAsync_Include_O2O_LoadsProfile()
    {
        await CreateEmployeeProfileTablesAsync();
        await ExecAsync("INSERT INTO el_employees VALUES (1, 'Alice')");
        await ExecAsync("INSERT INTO el_profiles VALUES (1, 1, 'Senior engineer')");

        await using var txn = await Factory.OpenTransactionAsync();
        var emp = await txn.GetAsync<ElEmployee>(1)
            .Include(e => e.Profile);

        Assert.NotNull(emp.Profile);
        Assert.Equal("Senior engineer", emp.Profile!.Bio);
    }

    /// <summary>
    /// Deep load cycle — loading a dept's members triggers recursive loading of each
    /// member's Dept relationship; the visited set prevents infinite recursion and the
    /// back-reference on each member is populated.
    /// </summary>
    [Fact]
    public async Task GetAsync_DeepLoad_MemberCarriesDeptBackRef_NoCycle()
    {
        await CreateDeptMemberTablesAsync();
        await ExecAsync("INSERT INTO el_depts VALUES (1, 'Engineering')");
        await ExecAsync("INSERT INTO el_members VALUES (1, 1, 'Alice')");

        await using var txn = await Factory.OpenTransactionAsync();
        var dept = await txn.GetAsync<ElDept>(1)
            .Include(d => d.Members);

        // No exception thrown — cycle was detected and cut off.
        Assert.NotNull(dept.Members);
        Assert.Single(dept.Members!);

        // The member's Dept back-reference is populated by the recursive load.
        var member = dept.Members![0];
        Assert.NotNull(member.Dept);
        Assert.Equal(1, member.Dept!.Id);
    }

    /// <summary>IncludeAll — loads all navigation properties without listing each one.</summary>
    [Fact]
    public async Task GetAsync_IncludeAll_LoadsAllNavigations()
    {
        await CreateDeptMemberTablesAsync();
        await ExecAsync("INSERT INTO el_depts VALUES (1, 'Engineering')");
        await ExecAsync("INSERT INTO el_members VALUES (1, 1, 'Alice')");
        await ExecAsync("INSERT INTO el_members VALUES (2, 1, 'Bob')");

        await using var txn = await Factory.OpenTransactionAsync();
        var dept = await txn.GetAsync<ElDept>(1).IncludeAll();

        Assert.NotNull(dept.Members);
        Assert.Equal(2, dept.Members!.Count);
    }
}
