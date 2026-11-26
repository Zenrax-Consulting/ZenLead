using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Ai;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Ai;

public class EfAiBudgetTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _ws = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly AiBudgetOptions _options = new() { MonthlyCostCapUsd = 10m, WarnAtFraction = 0.8m };

    public EfAiBudgetTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = _ws, Name = "Mine", CreatedAt = DateTime.UtcNow });
        ctx.Workspaces.Add(new Workspace { Id = _other, Name = "Other", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private void Spend(decimal usd, DateTime? at = null, Guid? workspace = null)
    {
        using var ctx = _db.CreateContext(null);
        ctx.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace ?? _ws, Model = "gpt-4o", EstimatedCostUsd = usd, CreatedAt = at ?? _clock.GetUtcNow().UtcDateTime
        });
        ctx.SaveChanges();
    }

    private async Task<AiBudgetStatus> StatusAsync()
    {
        await using var ctx = _db.CreateContext(null);                      // jobs and the API both pass the workspace explicitly
        return await new EfAiBudget(ctx, Options.Create(_options), _clock).GetStatusAsync(_ws, CancellationToken.None);
    }

    [Theory]
    [InlineData(0, BudgetState.Ok)]
    [InlineData(7.99, BudgetState.Ok)]
    [InlineData(8, BudgetState.Warning)]
    [InlineData(9.99, BudgetState.Warning)]
    [InlineData(10, BudgetState.Exceeded)]
    [InlineData(12.5, BudgetState.Exceeded)]
    public async Task State_follows_the_warning_and_cap_boundaries(double spent, BudgetState expected)
    {
        if (spent > 0) { Spend((decimal)spent / 2); Spend((decimal)spent / 2); }

        var status = await StatusAsync();

        Assert.Equal(expected, status.State);
        Assert.Equal((decimal)spent, status.SpentUsd);
        Assert.Equal(10m, status.CapUsd);
        Assert.Equal(new DateOnly(2026, 12, 1), status.ResetsOn);
    }

    [Fact]
    public async Task Only_this_utc_month_counts_and_the_next_month_starts_fresh()
    {
        Spend(9m, new DateTime(2026, 10, 31, 23, 59, 0, DateTimeKind.Utc));   // last month
        Spend(8m);
        Assert.Equal(BudgetState.Warning, (await StatusAsync()).State);

        _clock.Now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var december = await StatusAsync();
        Assert.Equal(BudgetState.Ok, december.State);
        Assert.Equal(0m, december.SpentUsd);
        Assert.Equal(new DateOnly(2027, 1, 1), december.ResetsOn);
    }

    [Fact]
    public async Task Another_workspaces_spend_is_ignored()
    {
        Spend(50m, workspace: _other);
        Assert.Equal(BudgetState.Ok, (await StatusAsync()).State);
    }

    [Fact]
    public async Task Usage_tracker_writes_purpose_campaign_enrollment_and_success()
    {
        var campaignId = Guid.NewGuid();
        var enrollmentId = Guid.NewGuid();
        await using (var ctx = _db.CreateContext(null))
        {
            var tracker = new EfTokenUsageTracker(ctx, NullLogger<EfTokenUsageTracker>.Instance);
            await tracker.RecordAsync(new AiUsageEntry(_ws, "gpt-4o", 10, 5, 0.1m, AiPurpose.Personalise, campaignId, enrollmentId, Succeeded: false));
            await tracker.RecordAsync(new AiUsageEntry(_ws, "gpt-4o", 10, 5, 0.1m));
        }

        await using var read = _db.CreateContext(_ws);
        var rows = read.AiUsageLogs.OrderBy(r => r.Purpose).ToList();
        Assert.Equal(AiPurpose.Compose, rows[0].Purpose);
        Assert.True(rows[0].Succeeded);
        Assert.Null(rows[0].CampaignId);
        Assert.Equal(AiPurpose.Personalise, rows[1].Purpose);
        Assert.False(rows[1].Succeeded);                                     // an explicit false is not swallowed by the column default
        Assert.Equal(campaignId, rows[1].CampaignId);
        Assert.Equal(enrollmentId, rows[1].EnrollmentId);
    }
}
