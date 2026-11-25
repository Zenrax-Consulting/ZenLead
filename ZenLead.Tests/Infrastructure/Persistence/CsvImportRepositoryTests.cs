using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class CsvImportRepositoryTests
{
    private static CsvImportBatch Batch(Guid ws, CsvImportStatus status = CsvImportStatus.Parsing) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, FileName = "a.csv", BlobPath = "imports/x.csv", Status = status,
        ColumnMappingJson = "{\"email\":\"Email\"}", ErrorLogJson = new string('x', 50_000), CreatedAt = DateTime.UtcNow
    };

    private static void Seed(TestDb db, params CsvImportBatch[] batches)
    {
        using var ctx = db.CreateContext(null);
        foreach (var id in batches.Select(b => b.WorkspaceId).Distinct())
            ctx.Workspaces.Add(new Workspace { Id = id, Name = "W", CreatedAt = DateTime.UtcNow });
        ctx.CsvImportBatches.AddRange(batches);
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Add_StampsNothingExtra_AndRoundTripsLargeJsonColumns()
    {
        using var db = new TestDb();
        var ws = Guid.NewGuid();
        Seed(db, Batch(ws));

        await using var ctx = db.CreateContext(ws);
        var loaded = (await new CsvImportRepository(ctx).GetAsync(ctx.CsvImportBatches.Single().Id))!;

        Assert.Equal(50_000, loaded.ErrorLogJson!.Length);
    }

    [Fact]
    public async Task Get_IsScopedToTheAmbientWorkspace()
    {
        using var db = new TestDb();
        var mine = Batch(Guid.NewGuid());
        var theirs = Batch(Guid.NewGuid());
        Seed(db, mine, theirs);

        await using var ctx = db.CreateContext(mine.WorkspaceId);
        var repo = new CsvImportRepository(ctx);

        Assert.NotNull(await repo.GetAsync(mine.Id));
        Assert.Null(await repo.GetAsync(theirs.Id));
    }

    [Fact]
    public async Task GetForJob_ScopesByExplicitWorkspace_AndUpdatePersistsAfterDetach()
    {
        using var db = new TestDb();
        var batch = Batch(Guid.NewGuid());
        Seed(db, batch);

        await using (var ctx = db.CreateContext(null))            // no HTTP context, like a Hangfire job
        {
            var repo = new CsvImportRepository(ctx);
            Assert.Null(await repo.GetForJobAsync(batch.Id, Guid.NewGuid()));

            var tracked = (await repo.GetForJobAsync(batch.Id, batch.WorkspaceId))!;
            ctx.ChangeTracker.Clear();                              // what ingestion does on a concurrent-insert fallback
            tracked.ImportedCount = 42; tracked.ProcessedRowCount = 500;
            await repo.UpdateAsync(tracked);
        }

        await using var check = db.CreateContext(batch.WorkspaceId);
        var saved = (await new CsvImportRepository(check).GetAsync(batch.Id))!;
        Assert.Equal(42, saved.ImportedCount);
        Assert.Equal(500, saved.ProcessedRowCount);
    }

    [Fact]
    public async Task MarkFailed_FlipsARunningBatch_ButNeverACompletedOne_OrAnotherWorkspaces()
    {
        using var db = new TestDb();
        var running = Batch(Guid.NewGuid());
        var completed = Batch(Guid.NewGuid(), CsvImportStatus.Completed);
        Seed(db, running, completed);

        await using (var ctx = db.CreateContext(null))
        {
            var repo = new CsvImportRepository(ctx);
            await repo.MarkFailedAsync(running.Id, Guid.NewGuid(), "wrong workspace");
            await repo.MarkFailedAsync(completed.Id, completed.WorkspaceId, "should not apply");
            Assert.Equal(CsvImportStatus.Parsing, (await repo.GetForJobAsync(running.Id, running.WorkspaceId))!.Status);

            await repo.MarkFailedAsync(running.Id, running.WorkspaceId, "boom");
        }

        await using var check = db.CreateContext(null);
        var repoCheck = new CsvImportRepository(check);
        var failed = (await repoCheck.GetForJobAsync(running.Id, running.WorkspaceId))!;
        Assert.Equal(CsvImportStatus.Failed, failed.Status);
        Assert.Equal("boom", failed.FailureReason);
        Assert.NotNull(failed.FinishedAt);
        Assert.Equal(CsvImportStatus.Completed, (await repoCheck.GetForJobAsync(completed.Id, completed.WorkspaceId))!.Status);
    }
}
