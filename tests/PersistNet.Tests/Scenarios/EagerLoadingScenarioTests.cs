using System.Collections.Generic;
using System.Linq;
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
///   el_topics    (Id PK, Title)             ← 3-level graph
///   el_posts     (Id PK, TopicId FK, Body)
///   el_comments  (Id PK, PostId FK, Text)
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

    /// <summary>
    /// LoadEntityGraphBatchAsync with multiple entities of the same type issues a single
    /// IN-clause query for all parents, correctly distributing children back to each parent.
    /// </summary>
    [Fact]
    public async Task LoadEntityGraphBatchAsync_MultipleParents_AllChildrenLoaded()
    {
        await CreateDeptMemberTablesAsync();
        // 3 depts, 2 members each.
        await ExecAsync("INSERT INTO el_depts VALUES (1, 'Dept1')");
        await ExecAsync("INSERT INTO el_depts VALUES (2, 'Dept2')");
        await ExecAsync("INSERT INTO el_depts VALUES (3, 'Dept3')");
        await ExecAsync("INSERT INTO el_members VALUES (1, 1, 'Alice')");
        await ExecAsync("INSERT INTO el_members VALUES (2, 1, 'Bob')");
        await ExecAsync("INSERT INTO el_members VALUES (3, 2, 'Carol')");
        await ExecAsync("INSERT INTO el_members VALUES (4, 2, 'Dave')");
        await ExecAsync("INSERT INTO el_members VALUES (5, 3, 'Eve')");
        await ExecAsync("INSERT INTO el_members VALUES (6, 3, 'Frank')");

        // Cast to Transaction to access the internal batch method directly.
        var txn = (Transaction)await Factory.OpenTransactionAsync();
        await using var _ = txn;

        var dept1 = await txn.LoadEntityCoreAsync<ElDept>(new object[] { 1 });
        var dept2 = await txn.LoadEntityCoreAsync<ElDept>(new object[] { 2 });
        var dept3 = await txn.LoadEntityCoreAsync<ElDept>(new object[] { 3 });

        // Batch-hydrate all three parents with one IN-clause query for members.
        await txn.LoadEntityGraphBatchAsync(
            new[] { (object)dept1, dept2, dept3 },
            new[] { "Members" },
            new HashSet<string>());

        Assert.Equal(2, dept1.Members!.Count);
        Assert.Equal(2, dept2.Members!.Count);
        Assert.Equal(2, dept3.Members!.Count);

        // Members must be routed to their correct parent.
        Assert.True(dept1.Members.All(m => m.DeptId == 1));
        Assert.True(dept2.Members.All(m => m.DeptId == 2));
        Assert.True(dept3.Members.All(m => m.DeptId == 3));

        Assert.Contains(dept1.Members, m => m.Name == "Alice");
        Assert.Contains(dept1.Members, m => m.Name == "Bob");
        Assert.Contains(dept2.Members, m => m.Name == "Carol");
        Assert.Contains(dept3.Members, m => m.Name == "Eve");
    }

    // ── 3-level deep graph ───────────────────────────────────────────────────

    [TableInfo(TableName = "el_topics")]
    private class ElTopic
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Title { get; set; } = "";

        [OneToManyRelationshipInfo(RelatedType = typeof(ElPost), MappedBy = "Topic")]
        public List<ElPost>? Posts { get; set; }
    }

    [TableInfo(TableName = "el_posts")]
    private class ElPost
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int TopicId { get; set; }

        [ColumnInfo]
        public string Body { get; set; } = "";

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(ElTopic),
            FromKeys = new[] { "TopicId" },
            ToKeys = new[] { "Id" })]
        public ElTopic? Topic { get; set; }

        [OneToManyRelationshipInfo(RelatedType = typeof(ElComment), MappedBy = "Post")]
        public List<ElComment>? Comments { get; set; }
    }

    [TableInfo(TableName = "el_comments")]
    private class ElComment
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public int PostId { get; set; }

        [ColumnInfo]
        public string Text { get; set; } = "";

        [ManyToOneRelationshipInfo(
            RelatedType = typeof(ElPost),
            FromKeys = new[] { "PostId" },
            ToKeys = new[] { "Id" })]
        public ElPost? Post { get; set; }
    }

    private async Task CreateTopicPostCommentTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE el_topics (Id INTEGER NOT NULL PRIMARY KEY, Title TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_posts " +
            "(Id INTEGER NOT NULL PRIMARY KEY, TopicId INTEGER NOT NULL, Body TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE el_comments " +
            "(Id INTEGER NOT NULL PRIMARY KEY, PostId INTEGER NOT NULL, Text TEXT NOT NULL)");
    }

    /// <summary>
    /// 3-level deep graph: loading a topic via Include(Posts) recursively loads
    /// each post's comments (level 3) through RecurseIntoRelatedAsync, and back-references
    /// are populated without infinite recursion.
    /// </summary>
    [Fact]
    public async Task GetAsync_Include_ThreeLevelGraph_LoadsAllLevels()
    {
        await CreateTopicPostCommentTablesAsync();
        await ExecAsync("INSERT INTO el_topics VALUES (1, 'Deep Loading')");
        await ExecAsync("INSERT INTO el_posts VALUES (1, 1, 'First post')");
        await ExecAsync("INSERT INTO el_comments VALUES (1, 1, 'Great post!')");
        await ExecAsync("INSERT INTO el_comments VALUES (2, 1, 'Agreed.')");

        await using var txn = await Factory.OpenTransactionAsync();
        var topic = await txn.GetAsync<ElTopic>(1)
            .Include(t => t.Posts);

        // Level 1 → Level 2: posts were loaded.
        Assert.NotNull(topic.Posts);
        Assert.Single(topic.Posts!);
        var post = topic.Posts![0];
        Assert.Equal("First post", post.Body);

        // Level 2 → Level 3: comments recursively loaded by RecurseIntoRelatedAsync.
        Assert.NotNull(post.Comments);
        Assert.Equal(2, post.Comments!.Count);
        Assert.Contains(post.Comments, c => c.Text == "Great post!");
        Assert.Contains(post.Comments, c => c.Text == "Agreed.");

        // Back-references are populated (cycle cut off — no infinite loop).
        Assert.NotNull(post.Topic);
        Assert.Equal(1, post.Topic!.Id);
    }

    /// <summary>
    /// IncludeAll — 3-level graph: loading a topic via IncludeAll() loads Posts (level 2)
    /// and recursively loads Comments inside each Post (level 3), without naming any
    /// navigation property explicitly.  Mirrors <see cref="GetAsync_Include_ThreeLevelGraph_LoadsAllLevels"/>.
    /// </summary>
    [Fact]
    public async Task GetAsync_IncludeAll_ThreeLevelGraph_LoadsAllLevels()
    {
        await CreateTopicPostCommentTablesAsync();
        await ExecAsync("INSERT INTO el_topics VALUES (1, 'Deep Loading')");
        await ExecAsync("INSERT INTO el_posts VALUES (1, 1, 'First post')");
        await ExecAsync("INSERT INTO el_comments VALUES (1, 1, 'Great post!')");
        await ExecAsync("INSERT INTO el_comments VALUES (2, 1, 'Agreed.')");

        await using var txn = await Factory.OpenTransactionAsync();
        var topic = await txn.GetAsync<ElTopic>(1).IncludeAll();

        // Level 1 → Level 2: posts loaded by IncludeAll.
        Assert.NotNull(topic.Posts);
        Assert.Single(topic.Posts!);
        var post = topic.Posts![0];
        Assert.Equal("First post", post.Body);

        // Level 2 → Level 3: comments recursively loaded by IncludeAll.
        Assert.NotNull(post.Comments);
        Assert.Equal(2, post.Comments!.Count);
        Assert.Contains(post.Comments, c => c.Text == "Great post!");
        Assert.Contains(post.Comments, c => c.Text == "Agreed.");

        // Back-references populated (cycle cut off — no infinite loop).
        Assert.NotNull(post.Topic);
        Assert.Equal(1, post.Topic!.Id);
    }
}
