using Microsoft.Extensions.Logging.Abstractions;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Leads;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Leads;

namespace ZenLead.Tests.Application.Discovery;

public class ProcessDiscoveryRunUseCaseTests
{
    private readonly Guid _ws = Guid.NewGuid();
    private readonly FakeDiscoveryRunRepository _runs = new();
    private readonly FakeLeadIngestionStore _store = new();
    private readonly List<TimeSpan> _delays = [];

    private LeadDiscoveryRun SeedRun(int requested = 100, DiscoveryRunStatus status = DiscoveryRunStatus.Queued, Guid? workspace = null)
    {
        var run = new LeadDiscoveryRun
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace ?? _ws, Provider = "Scripted", RequestedCount = requested, Status = status,
            CriteriaJson = LeadCriteriaJson.Serialize(new LeadSearchCriteria([], [], ["US"], null, null, [])), CreatedAt = DateTime.UtcNow
        };
        _runs.Runs.Add(run);
        return run;
    }

    private ProcessDiscoveryRunUseCase Build(ILeadSource source, LeadSourceOptions? options = null)
        => new(_runs, source, new LeadIngestionService(_store), options ?? new LeadSourceOptions(), TimeProvider.System,
               NullLogger<ProcessDiscoveryRunUseCase>.Instance)
        { Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; } };

    private void SeedExisting(string email, LeadStatus status = LeadStatus.New)
        => _store.Leads.Add(new Lead { Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "Existing", Email = email, Status = status, CreatedAt = DateTime.UtcNow });

    [Fact]
    public async Task Page_CountsEveryOutcome_AndTagsImportedLeads()
    {
        SeedExisting("dup@acme.com");
        SeedExisting("gone@acme.com", LeadStatus.Unsubscribed);
        SeedExisting("bounced@acme.com", LeadStatus.Bounced);
        var source = new ScriptedLeadSource(new LeadSearchPage([
            ScriptedLeadSource.Lead("new@acme.com"),
            ScriptedLeadSource.Lead(null),
            ScriptedLeadSource.Lead("dup@acme.com"),
            ScriptedLeadSource.Lead("gone@acme.com"),
            ScriptedLeadSource.Lead("bounced@acme.com"),
            ScriptedLeadSource.Lead("bad@acme.com", EmailVerificationStatus.Invalid)], null, 6));
        var run = SeedRun();

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
        Assert.Equal(6, run.FoundCount);
        Assert.Equal(1, run.ImportedCount);
        Assert.Equal(1, run.NoEmailCount);
        Assert.Equal(1, run.SkippedDuplicateCount);
        Assert.Equal(2, run.SkippedSuppressedCount);
        Assert.Equal(6, run.CreditsUsed);
        var imported = Assert.Single(_store.Leads, l => l.Email == "new@acme.com");
        Assert.Equal(LeadSource.Discovery, imported.Source);
        Assert.Equal(run.Id, imported.SourceRunId);
        Assert.Equal(EmailVerificationStatus.Verified, imported.EmailVerificationStatus);
        Assert.DoesNotContain(_store.Leads, l => l.Email == "bad@acme.com");
    }

    [Theory]
    [InlineData(DiscoveryRunStatus.Completed)]
    [InlineData(DiscoveryRunStatus.CapReached)]
    public async Task FinishedRun_IsANoOp(DiscoveryRunStatus status)
    {
        var source = new ScriptedLeadSource();
        var run = SeedRun(status: status);

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Empty(source.Calls);
        Assert.Equal(status, run.Status);
    }

    [Fact]
    public async Task OtherWorkspacesRun_IsNotFound_AndNothingHappens()
    {
        var source = new ScriptedLeadSource();
        var run = SeedRun(workspace: Guid.NewGuid());

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Empty(source.Calls);
        Assert.Equal(DiscoveryRunStatus.Queued, run.Status);
    }

    [Fact]
    public async Task InterruptedRun_ResumesFromCursor_WithoutDuplicates()
    {
        var page0 = new LeadSearchPage([ScriptedLeadSource.Lead("a@acme.com")], "1", 1);
        var page1 = new LeadSearchPage([ScriptedLeadSource.Lead("b@acme.com")], null, 1);
        var source = new ScriptedLeadSource(page0, page1);
        var run = SeedRun();
        // page 0 was already imported and persisted before the interruption
        SeedExisting("a@acme.com");
        run.Status = DiscoveryRunStatus.Running; run.Cursor = "1"; run.PagesFetched = 1; run.ImportedCount = 1;

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal("1", Assert.Single(source.Calls).Cursor);
        Assert.Equal(2, run.ImportedCount);
        Assert.Equal(2, _store.Leads.Count);
        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task RunningTwice_YieldsTheSameLeadCount()
    {
        var source = new ScriptedLeadSource(new LeadSearchPage([ScriptedLeadSource.Lead("a@acme.com"), ScriptedLeadSource.Lead("b@acme.com")], null, 2));
        var run = SeedRun();
        var useCase = Build(source);

        await useCase.ExecuteAsync(run.Id, _ws, CancellationToken.None);
        await useCase.ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(2, _store.Leads.Count);
        Assert.Single(source.Calls);
    }

    [Fact]
    public async Task CreditCap_StopsBeforeTheNextPage_AndNeverCallsThirdTime()
    {
        var source = new ScriptedLeadSource { GeneratedCreditsPerLead = 1 };
        var options = new LeadSourceOptions { MonthlyCreditCap = 30, PageSize = 25 };
        var run = SeedRun(requested: 100);

        await Build(source, options).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.CapReached, run.Status);
        Assert.Equal(2, source.Calls.Count);
        Assert.Equal("Monthly discovery credit cap reached.", run.FailureReason);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task CreditCap_CountsOnlyThisWorkspace()
    {
        _runs.Runs.Add(new LeadDiscoveryRun { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), CreditsUsed = 10_000, CreatedAt = DateTime.UtcNow });
        var source = new ScriptedLeadSource(new LeadSearchPage([ScriptedLeadSource.Lead("a@acme.com")], null, 1));
        var run = SeedRun();

        await Build(source, new LeadSourceOptions { MonthlyCreditCap = 30 }).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task TransientFailureThenSuccess_RetriesWithBackoff()
    {
        var source = new ScriptedLeadSource(new LeadSearchPage([ScriptedLeadSource.Lead("a@acme.com")], null, 1));
        source.FailuresToThrow.Enqueue(new LeadSourceException(LeadSourceFailureKind.RateLimited, "429"));
        source.FailuresToThrow.Enqueue(new LeadSourceException(LeadSourceFailureKind.Unavailable, "503"));
        var run = SeedRun();

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)], _delays);
        Assert.Equal(1, run.ImportedCount);
    }

    [Fact]
    public async Task TransientFourTimes_FailsWithFriendlyReason()
    {
        var source = new ScriptedLeadSource();
        for (var i = 0; i < 4; i++) source.FailuresToThrow.Enqueue(new LeadSourceException(LeadSourceFailureKind.Unavailable, "secret provider detail"));
        var run = SeedRun();

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Failed, run.Status);
        Assert.Equal(3, _delays.Count);
        Assert.Equal(4, source.Calls.Count);
        Assert.DoesNotContain("secret", run.FailureReason);
    }

    [Theory]
    [InlineData(LeadSourceFailureKind.OutOfCredits)]
    [InlineData(LeadSourceFailureKind.Unauthorized)]
    [InlineData(LeadSourceFailureKind.InvalidResponse)]
    public async Task NonTransientFailure_FailsImmediately_WithoutRetry(LeadSourceFailureKind kind)
    {
        var source = new ScriptedLeadSource();
        source.FailuresToThrow.Enqueue(new LeadSourceException(kind, "boom"));
        var run = SeedRun();

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Failed, run.Status);
        Assert.Empty(_delays);
        Assert.Single(source.Calls);
        Assert.False(string.IsNullOrEmpty(run.FailureReason));
    }

    [Fact]
    public async Task StopsAtRequestedCount_WithinDocumentedOvershoot()
    {
        var source = new ScriptedLeadSource();
        var run = SeedRun(requested: 10);

        await Build(source).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
        Assert.InRange(run.ImportedCount, 10, 15);
        Assert.Single(source.Calls);
        Assert.Equal(15, source.Calls[0].Limit);      // remaining + 5
    }

    [Fact]
    public async Task AllDuplicates_StopsAtMaxPages()
    {
        var emails = Enumerable.Range(0, 5).Select(i => $"x{i}@acme.com").ToList();
        emails.ForEach(e => SeedExisting(e));
        var page = new LeadSearchPage(emails.Select(e => ScriptedLeadSource.Lead(e)).ToList(), "0", 5);
        var source = new ScriptedLeadSource(page);
        var run = SeedRun(requested: 10);          // maxPages = 10 * 5 / 25 = 2

        await Build(source, new LeadSourceOptions { PageSize = 25, MaxPagesFactor = 5 }).ExecuteAsync(run.Id, _ws, CancellationToken.None);

        Assert.Equal(2, source.Calls.Count);
        Assert.Equal(DiscoveryRunStatus.Completed, run.Status);
        Assert.Equal(0, run.ImportedCount);
        Assert.Equal(10, run.SkippedDuplicateCount);
    }
}
