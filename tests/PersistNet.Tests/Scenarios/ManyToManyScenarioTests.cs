using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace PersistNet.Tests.Scenarios;

/// <summary>
/// Scenario: Many-to-Many relationship via an explicit join table.
///
/// Schema:
///   sc_m2m_actors        (Id, Name)
///   sc_m2m_movies        (Id, Title)
///   sc_m2m_castings      (ActorId FK→actors.Id, MovieId FK→movies.Id, PK composite)
///
/// Ownership: M2mActor.Movies is the owning side — join rows are created when
///            saving from this side.
///            M2mMovie.Actors is the inverse side (MappedBy = "Movies").
/// </summary>
public sealed class ManyToManyScenarioTests : ScenarioTestBase
{
    // ── Entity model ────────────────────────────────────────────────────────

    [TableInfo(TableName = "sc_m2m_actors")]
    private class M2mActor
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Name { get; set; } = "";

        [ManyToManyRelationshipInfo(
            RelatedType = typeof(M2mMovie),
            JoinTableName = "sc_m2m_castings",
            LeftKeyColumns = new[] { "ActorId" },
            RightKeyColumns = new[] { "MovieId" },
            LeftForeignKeys = new[] { "Id" },
            RightForeignKeys = new[] { "Id" })]
        public List<M2mMovie>? Movies { get; set; }
    }

    [TableInfo(TableName = "sc_m2m_movies")]
    private class M2mMovie
    {
        [ColumnInfo(Key = true)]
        public int Id { get; set; }

        [ColumnInfo]
        public string Title { get; set; } = "";

        [ManyToManyRelationshipInfo(RelatedType = typeof(M2mActor), MappedBy = "Movies")]
        public List<M2mActor>? Actors { get; set; }
    }

    // ── DDL ─────────────────────────────────────────────────────────────────

    private async Task CreateTablesAsync()
    {
        await ExecAsync(
            "CREATE TABLE sc_m2m_actors (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE sc_m2m_movies (Id INTEGER NOT NULL PRIMARY KEY, Title TEXT NOT NULL)");
        await ExecAsync(
            "CREATE TABLE sc_m2m_castings " +
            "(ActorId INTEGER NOT NULL, MovieId INTEGER NOT NULL, PRIMARY KEY (ActorId, MovieId))");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save one actor appearing in two movies.
    /// Expects: 1 actor row, 2 movie rows, 2 join rows.
    /// </summary>
    [Fact]
    public async Task Save_OneActorInTwoMovies_TwoJoinRowsCreated()
    {
        await CreateTablesAsync();
        // Pre-insert entities with known IDs so the ORM can UPDATE them (non-zero PK).
        // The M2M join rows are what this test actually exercises.
        await ExecAsync("INSERT INTO sc_m2m_actors VALUES (1, 'Tom Hanks')");
        await ExecAsync("INSERT INTO sc_m2m_movies VALUES (1, 'Cast Away')");
        await ExecAsync("INSERT INTO sc_m2m_movies VALUES (2, 'Forrest Gump')");

        var movie1 = new M2mMovie { Id = 1, Title = "Cast Away" };
        var movie2 = new M2mMovie { Id = 2, Title = "Forrest Gump" };
        var actor  = new M2mActor
        {
            Id = 1, Name = "Tom Hanks",
            Movies = new List<M2mMovie> { movie1, movie2 }
        };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(actor);
        await txn.CommitAsync();

        Assert.Equal(1L, await CountAsync("sc_m2m_actors"));
        Assert.Equal(2L, await CountAsync("sc_m2m_movies"));
        Assert.Equal(2L, await CountAsync("sc_m2m_castings"));

        // Verify join rows reference the actor correctly.
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sc_m2m_castings WHERE ActorId = 1")));
    }

    /// <summary>
    /// Save two actors each referencing the same movie.
    /// The movie row must be inserted once; both join rows must be created.
    /// </summary>
    [Fact]
    public async Task Save_TwoActorsInSameMovie_MovieInsertedOnce_TwoJoinRows()
    {
        await CreateTablesAsync();
        // Pre-insert entities with known IDs so the ORM can UPDATE them.
        await ExecAsync("INSERT INTO sc_m2m_actors VALUES (1, 'Keanu Reeves')");
        await ExecAsync("INSERT INTO sc_m2m_actors VALUES (2, 'Laurence Fishburne')");
        await ExecAsync("INSERT INTO sc_m2m_movies VALUES (1, 'The Matrix')");

        var movie  = new M2mMovie { Id = 1, Title = "The Matrix" };
        var actor1 = new M2mActor { Id = 1, Name = "Keanu Reeves",      Movies = new List<M2mMovie> { movie } };
        var actor2 = new M2mActor { Id = 2, Name = "Laurence Fishburne", Movies = new List<M2mMovie> { movie } };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Save(actor1);
        txn.Save(actor2);
        await txn.CommitAsync();

        Assert.Equal(2L, await CountAsync("sc_m2m_actors"));
        Assert.Equal(1L, await CountAsync("sc_m2m_movies"));
        Assert.Equal(2L, await CountAsync("sc_m2m_castings"));
    }

    /// <summary>
    /// Delete an actor: join rows are deleted first (the actor owns the join table FK),
    /// then the actor row.
    /// Movie rows must remain intact.
    /// </summary>
    [Fact]
    public async Task Delete_Actor_JoinRowsDeletedBeforeActor()
    {
        await CreateTablesAsync();
        await ExecAsync("INSERT INTO sc_m2m_actors VALUES (1, 'Brad Pitt')");
        await ExecAsync("INSERT INTO sc_m2m_movies VALUES (1, 'Fight Club')");
        await ExecAsync("INSERT INTO sc_m2m_movies VALUES (2, 'Se7en')");
        await ExecAsync("INSERT INTO sc_m2m_castings VALUES (1, 1)");
        await ExecAsync("INSERT INTO sc_m2m_castings VALUES (1, 2)");

        var movie1 = new M2mMovie { Id = 1 };
        var movie2 = new M2mMovie { Id = 2 };
        var actor  = new M2mActor { Id = 1, Movies = new List<M2mMovie> { movie1, movie2 } };

        await using var txn = await Factory.OpenTransactionAsync();
        txn.Delete(actor);
        await txn.CommitAsync();

        Assert.Equal(0L, await CountAsync("sc_m2m_castings"));
        Assert.Equal(0L, await CountAsync("sc_m2m_actors"));
        Assert.Equal(2L, await CountAsync("sc_m2m_movies")); // movies unaffected
    }
}
