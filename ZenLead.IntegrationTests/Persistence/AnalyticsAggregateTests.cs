using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Dtos.Analytics;
using ZenLead.Application.UseCases.Analytics;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Infrastructure.Testing;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>The API with the super admin config the seed scenarios need.</summary>
public sealed class SeededApiFactory : IntegrationApiFactory
{
    public SeededApiFactory()
    {
        Settings["SuperAdmin:Email"] = "platform-admin@example.test";
        Settings["SuperAdmin:Password"] = ApiFactory.NewPassword();
    }

    public Task<SeedResult> SeedAsync(string scenario)
        => InScopeAsync(sp => ActivatorUtilities.CreateInstance<SeedScenarios>(sp).RunAsync(scenario));
}

/// <summary>
/// F28 aggregates as SQL Server computes them (GROUP BY, COUNT DISTINCT, 15-minute buckets) must equal a plain in-memory LINQ
/// recompute over the same rows. The <c>analytics</c> seed is taken with the clock on 10 Nov 2026, so the 30-day range crosses the
/// Europe/London DST change on 25 Oct and the trend has to zero-fill and count that day exactly once.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class AnalyticsAggregateTests(SeededApiFactory factory) : IClassFixture<SeededApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset SeedTime = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById(SeedScenarios.MainTimeZone);
    private SeedResult _seed = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        factory.Clock.Set(SeedTime);
        _seed = await factory.SeedAsync("analytics");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(SeedTime.UtcDateTime, London));
    private static AnalyticsQuery Last31Days => new(Today.AddDays(-30), Today);

    [Fact]
    public void TheSeed_CrossesADstChange()
    {
        Assert.True(_seed.DstChangeInAnalyticsRange);
        Assert.NotEqual(London.GetUtcOffset(SeedTime.UtcDateTime.AddDays(-30)), London.GetUtcOffset(SeedTime.UtcDateTime));
    }

    [Fact]
    public async Task WorkspaceTotalsAndTrend_EqualAnInMemoryRecompute()
    {
        var ws = _seed.WorkspaceId!.Value;
        var outcome = await factory.InScopeAsync(sp => sp.GetRequiredService<AnalyticsUseCase>().WorkspaceAsync(ws, Last31Days, CancellationToken.None));
        Assert.Equal(AnalyticsOutcomeKind.Ok, outcome.Kind);
        var actual = outcome.Value!;

        var expected = await RecomputeAsync(ws, campaignId: null, Last31Days);

        Assert.True(expected.Sent > 0, "the seed should have sends in range");
        Assert.Equal(expected.Totals(), actual.Totals with { DeliveredRate = null, OpenRate = null, ReplyRate = null, BounceRate = null });
        Assert.Equal(expected.Trend, actual.Trend);
        Assert.Equal(actual.Totals.Sent, actual.Trend.Sum(p => p.Sent));
    }

    [Fact]
    public async Task Trend_HasEveryLocalDayOnce_IncludingTheDstDay_AndZeroFillsQuietDays()
    {
        var ws = _seed.WorkspaceId!.Value;
        var actual = (await factory.InScopeAsync(sp => sp.GetRequiredService<AnalyticsUseCase>().WorkspaceAsync(ws, Last31Days, CancellationToken.None))).Value!;

        var days = actual.Trend.Select(p => p.Day).ToList();
        Assert.Equal(31, days.Count);
        Assert.Equal(days.Distinct().Count(), days.Count);
        Assert.Equal(Enumerable.Range(0, 31).Select(i => Today.AddDays(-30 + i)), days);
        Assert.Single(actual.Trend, p => p.Day == new DateOnly(2026, 10, 25));
        Assert.Contains(actual.Trend, p => p.Sent == 0);                   // sends are 36 h apart, so some days are empty
    }

    [Fact]
    public async Task CampaignTotalsAndSteps_EqualAnInMemoryRecompute()
    {
        var ws = _seed.WorkspaceId!.Value;
        var campaignId = _seed.AnalyticsCampaignId!.Value;
        var outcome = await factory.InScopeAsync(sp => sp.GetRequiredService<AnalyticsUseCase>().CampaignAsync(ws, campaignId, Last31Days, CancellationToken.None));
        Assert.Equal(AnalyticsOutcomeKind.Ok, outcome.Kind);
        var actual = outcome.Value!;

        var expected = await RecomputeAsync(ws, campaignId, Last31Days);

        Assert.Equal(expected.Totals(), actual.Totals with { DeliveredRate = null, OpenRate = null, ReplyRate = null, BounceRate = null });
        Assert.Equal(expected.Trend, actual.Trend);
        Assert.Equal(actual.Totals.Sent, actual.Steps.Sum(s => s.Sent));
        Assert.Equal(actual.Totals.Opened, actual.Steps.Sum(s => s.Opened));
    }

    [Fact]
    public async Task AnotherWorkspace_SeesNoneOfIt()
    {
        var other = _seed.OtherWorkspaceId!.Value;
        var actual = (await factory.InScopeAsync(sp => sp.GetRequiredService<AnalyticsUseCase>().WorkspaceAsync(other, Last31Days, CancellationToken.None))).Value!;
        var campaign = await factory.InScopeAsync(sp => sp.GetRequiredService<AnalyticsUseCase>()
            .CampaignAsync(other, _seed.AnalyticsCampaignId!.Value, Last31Days, CancellationToken.None));

        Assert.Equal(0, actual.Totals.Sent);
        Assert.Empty(actual.Campaigns);
        Assert.Equal(AnalyticsOutcomeKind.NotFound, campaign.Kind);
    }

    private sealed record Expected(int Sent, int Delivered, int Opened, int Bounced, int Contacted, int Replied, IReadOnlyList<TrendPoint> Trend)
    {
        public AnalyticsTotals Totals() => new(Sent, Delivered, Opened, Bounced, Contacted, Replied, null, null, null, null);
    }

    /// <summary>The F28 definitions, applied with LINQ to objects over every row of the workspace.</summary>
    private async Task<Expected> RecomputeAsync(Guid ws, Guid? campaignId, AnalyticsQuery query)
    {
        await using var db = factory.Sql.CreateContext();
        var messages = await db.EmailMessages.IgnoreQueryFilters().Where(m => m.WorkspaceId == ws).AsNoTracking().ToListAsync();
        var inbound = await db.InboxMessages.IgnoreQueryFilters().Where(m => m.WorkspaceId == ws).AsNoTracking().ToListAsync();
        var threads = await db.InboxThreads.IgnoreQueryFilters().Where(t => t.WorkspaceId == ws).AsNoTracking().ToListAsync();

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(query.From!.Value.ToDateTime(TimeOnly.MinValue), London);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(query.To!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), London);
        bool InRange(DateTime? t) => t is { } v && v >= fromUtc && v < toUtc;
        DateOnly LocalDay(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), London));

        var cohort = messages.Where(m => InRange(m.SentAt) && (campaignId is null || m.CampaignId == campaignId)).ToList();
        var cohortEnrollments = cohort.Select(m => m.EnrollmentId).ToHashSet();
        var firstReplies = (from i in inbound
                            where i.Direction == MessageDirection.Inbound && !i.IsAutoReply
                            join t in threads on i.ThreadId equals t.Id
                            where t.EnrollmentId is { } e && cohortEnrollments.Contains(e)
                            group i.OccurredAt by t.EnrollmentId!.Value into g
                            select g.Min()).ToList();

        var trend = Enumerable.Range(0, query.To.Value.DayNumber - query.From.Value.DayNumber + 1).Select(i =>
        {
            var day = query.From.Value.AddDays(i);
            return new TrendPoint(day,
                cohort.Count(m => LocalDay(m.SentAt!.Value) == day),
                cohort.Count(m => InRange(m.OpenedAt) && LocalDay(m.OpenedAt!.Value) == day),
                firstReplies.Count(r => InRange(r) && LocalDay(r) == day));
        }).ToList();

        return new Expected(
            cohort.Count,
            cohort.Count(m => m.DeliveredAt != null),
            cohort.Count(m => m.OpenedAt != null),
            cohort.Count(m => m.BouncedAt != null || m.Status is EmailMessageStatus.Bounced or EmailMessageStatus.Dropped),
            cohortEnrollments.Count,
            firstReplies.Count,
            trend);
    }
}
