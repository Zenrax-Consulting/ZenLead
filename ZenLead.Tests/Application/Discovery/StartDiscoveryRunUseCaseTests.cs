using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Discovery;

public class StartDiscoveryRunUseCaseTests
{
    private readonly Guid _ws = Guid.NewGuid();
    private readonly FakeTargetProfileRepository _profiles = new();
    private readonly FakeDiscoveryRunRepository _runs = new();
    private readonly LeadSourceOptions _options = new() { MonthlyCreditCap = 100, MaxLeadsPerRun = 50 };
    private readonly FakeJobScheduler _scheduler;

    public StartDiscoveryRunUseCaseTests() => _scheduler = new FakeJobScheduler(_runs);

    private StartDiscoveryRunUseCase Build() => new(_profiles, _runs, new ScriptedLeadSource(), _scheduler, _options, TimeProvider.System);

    private TargetProfile Profile(Guid? workspace = null)
    {
        var p = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = workspace ?? _ws, Name = "P", CriteriaJson = "{}" };
        _profiles.Profiles.Add(p);
        return p;
    }

    [Fact]
    public async Task OtherTenantsProfile_IsNotFound()
    {
        var profile = Profile(Guid.NewGuid());

        var result = await Build().ExecuteAsync(_ws, Guid.NewGuid(), profile.Id, 10, CancellationToken.None);

        Assert.Equal(StartRunOutcome.NotFound, result.Outcome);
        Assert.Empty(_scheduler.Enqueued);
    }

    [Fact]
    public async Task SecondStartWhileActive_IsAlreadyRunning()
    {
        var profile = Profile();
        var useCase = Build();

        var first = await useCase.ExecuteAsync(_ws, Guid.NewGuid(), profile.Id, 10, CancellationToken.None);
        var second = await useCase.ExecuteAsync(_ws, Guid.NewGuid(), profile.Id, 10, CancellationToken.None);

        Assert.Equal(StartRunOutcome.Started, first.Outcome);
        Assert.Equal(StartRunOutcome.AlreadyRunning, second.Outcome);
        Assert.Single(_scheduler.Enqueued);
    }

    [Fact]
    public async Task JobIsEnqueuedOnce_AfterTheRunRowExists()
    {
        var profile = Profile();

        var result = await Build().ExecuteAsync(_ws, Guid.NewGuid(), profile.Id, 10, CancellationToken.None);

        var enqueued = Assert.Single(_scheduler.Enqueued);
        Assert.Equal(result.Run!.Id, enqueued.RunId);
        Assert.Equal(_ws, enqueued.WorkspaceId);
        Assert.True(enqueued.RunRowExisted);
        Assert.Equal(DiscoveryRunStatus.Queued, result.Run.Status);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10, 10)]
    [InlineData(5000, 50)]
    public async Task MaxLeads_IsClamped(int requested, int expected)
    {
        var profile = Profile();

        var result = await Build().ExecuteAsync(_ws, Guid.NewGuid(), profile.Id, requested, CancellationToken.None);

        Assert.Equal(expected, result.Run!.RequestedCount);
    }

    [Fact]
    public async Task WorkspaceAtCap_CannotStart_ButAnotherWorkspaceCan()
    {
        _runs.Runs.Add(new LeadDiscoveryRun { Id = Guid.NewGuid(), WorkspaceId = _ws, CreditsUsed = 100, Status = DiscoveryRunStatus.Completed, CreatedAt = DateTime.UtcNow });
        var mine = Profile();
        var other = Guid.NewGuid();
        var theirs = Profile(other);

        var blocked = await Build().ExecuteAsync(_ws, Guid.NewGuid(), mine.Id, 10, CancellationToken.None);
        var allowed = await Build().ExecuteAsync(other, Guid.NewGuid(), theirs.Id, 10, CancellationToken.None);

        Assert.Equal(StartRunOutcome.CapReached, blocked.Outcome);
        Assert.Equal(StartRunOutcome.Started, allowed.Outcome);
    }

    [Fact]
    public void MonthStart_IsFirstOfMonthUtc()
        => Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                        StartDiscoveryRunUseCase.MonthStart(new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero)));
}
