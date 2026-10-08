using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class LeadDiscoveryRunPersistenceTests
{
    private static readonly DateTime Oct1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void Workspaces(ZenLeadDbContext ctx, params Guid[] ids)
        => ctx.Workspaces.AddRange(ids.Select(id => new Workspace { Id = id, Name = "W", CreatedAt = DateTime.UtcNow }));

    private static LeadDiscoveryRun Run(Guid ws, DateTime createdAt, int credits, DiscoveryRunStatus status = DiscoveryRunStatus.Completed) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, Provider = "Fake", CreatedAt = createdAt, CreditsUsed = credits, Status = status
    };

    [Fact]
    public async Task MonthlyCredits_RespectMonthBoundaryAndWorkspace()
    {
        using var db = new TestDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using (var seed = db.CreateContext(null))
        {
            Workspaces(seed, a, b);
            seed.DiscoveryRuns.AddRange(
                Run(a, Oct1.AddSeconds(-1), 1000),      // September: excluded
                Run(a, Oct1, 10),                        // exactly at month start: included
                Run(a, Oct1.AddDays(20), 5),
                Run(b, Oct1.AddDays(2), 999));           // other workspace: excluded
            await seed.SaveChangesAsync();
        }

        await using var ctx = db.CreateContext(null);          // no HTTP context, like a Hangfire job
        var repo = new DiscoveryRunRepository(ctx);

        Assert.Equal(15, await repo.GetCreditsUsedThisMonthAsync(a, Oct1));
        Assert.Equal(999, await repo.GetCreditsUsedThisMonthAsync(b, Oct1));
        Assert.Equal(0, await repo.GetCreditsUsedThisMonthAsync(Guid.NewGuid(), Oct1));
    }

    [Fact]
    public async Task HasActiveRun_OnlyForQueuedOrRunning_InThatWorkspace()
    {
        using var db = new TestDb();
        var a = Guid.NewGuid();
        using (var seed = db.CreateContext(null))
        {
            Workspaces(seed, a);
            seed.DiscoveryRuns.AddRange(Run(a, Oct1, 0, DiscoveryRunStatus.Completed), Run(a, Oct1, 0, DiscoveryRunStatus.Failed));
            await seed.SaveChangesAsync();
        }
        await using var ctx = db.CreateContext(null);
        var repo = new DiscoveryRunRepository(ctx);
        Assert.False(await repo.HasActiveRunAsync(a));

        ctx.DiscoveryRuns.Add(Run(a, Oct1, 0, DiscoveryRunStatus.Running));
        await ctx.SaveChangesAsync();

        Assert.True(await repo.HasActiveRunAsync(a));
        Assert.False(await repo.HasActiveRunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetForJob_ScopesByExplicitWorkspace_AndUpdatePersistsAfterDetach()
    {
        using var db = new TestDb();
        var a = Guid.NewGuid();
        var run = Run(a, Oct1, 0, DiscoveryRunStatus.Queued);
        using (var seed = db.CreateContext(null)) { Workspaces(seed, a); seed.DiscoveryRuns.Add(run); await seed.SaveChangesAsync(); }

        await using var ctx = db.CreateContext(null);
        var repo = new DiscoveryRunRepository(ctx);
        Assert.Null(await repo.GetForJobAsync(run.Id, Guid.NewGuid()));
        var loaded = await repo.GetForJobAsync(run.Id, a);
        Assert.NotNull(loaded);

        ctx.ChangeTracker.Clear();                              // what lead ingestion does on a concurrent-insert fallback
        loaded!.ImportedCount = 7;
        await repo.UpdateAsync(loaded);

        await using var check = db.CreateContext(a);
        Assert.Equal(7, (await new DiscoveryRunRepository(check).GetAsync(run.Id))!.ImportedCount);
    }

    [Fact]
    public async Task DeletingAProfile_NullsTheRunsProfileId()
    {
        using var db = new TestDb();
        var ws = Guid.NewGuid();
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "P", CreatedAt = Oct1 };
        var run = Run(ws, Oct1, 0);
        run.TargetProfileId = profile.Id;
        using (var seed = db.CreateContext(null)) { Workspaces(seed, ws); seed.TargetProfiles.Add(profile); seed.DiscoveryRuns.Add(run); await seed.SaveChangesAsync(); }

        await using (var ctx = db.CreateContext(ws))
        {
            await new TargetProfileRepository(ctx).DeleteAsync((await new TargetProfileRepository(ctx).GetAsync(profile.Id))!);
        }

        await using var check = db.CreateContext(ws);
        Assert.Null((await new DiscoveryRunRepository(check).GetAsync(run.Id))!.TargetProfileId);
    }
}
