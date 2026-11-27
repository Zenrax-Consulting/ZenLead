using ZenLead.Application.Dtos.Analytics;
using ZenLead.Application.UseCases.Analytics;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

/// <summary>F28 analytics over the real <see cref="AnalyticsRepository"/> on SQLite: cohort semantics, unique opens, replies, steps, trend, isolation.</summary>
public sealed class AnalyticsRepositoryTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _ny = Guid.NewGuid();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 10);
    private static DateTime Day(int day, int hour = 10) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    public AnalyticsRepositoryTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.AddRange(
            new Workspace { Id = _a, Name = "A", TimeZone = "UTC", CreatedAt = DateTime.UtcNow },
            new Workspace { Id = _b, Name = "B", TimeZone = "UTC", CreatedAt = DateTime.UtcNow },
            new Workspace { Id = _ny, Name = "NY", TimeZone = "America/New_York", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class Builder(ZenLeadDbContext ctx, Guid ws)
    {
        public Campaign Campaign { get; } = new() { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Spring", Status = CampaignStatus.Active, CreatedAt = DateTime.UtcNow };
        public List<CampaignStep> Steps { get; } = [];

        public Builder WithSteps(int n, string name = "Spring")
        {
            Campaign.Name = name;
            for (var i = 1; i <= n; i++) Steps.Add(new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = Campaign.Id, Order = i, SubjectTemplate = $"Subject {i}", BodyTemplate = "b" });
            ctx.Campaigns.Add(Campaign);
            ctx.CampaignSteps.AddRange(Steps);
            return this;
        }

        /// <summary>A lead + enrollment, with one message per (step order, sentAt, configure).</summary>
        public (CampaignEnrollment Enrollment, List<EmailMessage> Messages) Lead(params (int Step, DateTime SentAt, Action<EmailMessage>? Configure)[] sends)
        {
            var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "L", Email = $"{Guid.NewGuid():N}@x.test", Status = LeadStatus.Contacted, CreatedAt = DateTime.UtcNow };
            var e = new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = Campaign.Id, LeadId = lead.Id, EnrolledAt = DateTime.UtcNow };
            ctx.Leads.Add(lead);
            ctx.CampaignEnrollments.Add(e);
            var messages = sends.Select(s =>
            {
                var m = new EmailMessage
                {
                    Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = Campaign.Id, EnrollmentId = e.Id, StepId = Steps[s.Step - 1].Id, LeadId = lead.Id,
                    Subject = "s", Body = "b", Status = EmailMessageStatus.Sent, QueuedAt = s.SentAt, SentAt = s.SentAt
                };
                s.Configure?.Invoke(m);
                return m;
            }).ToList();
            ctx.EmailMessages.AddRange(messages);
            return (e, messages);
        }

        public void Reply(CampaignEnrollment e, DateTime at, bool auto = false)
        {
            var thread = ctx.InboxThreads.Local.FirstOrDefault(t => t.EnrollmentId == e.Id);
            if (thread is null)
            {
                thread = new InboxThread { Id = Guid.NewGuid(), WorkspaceId = ws, LeadId = e.LeadId, CampaignId = e.CampaignId, EnrollmentId = e.Id, Subject = "s", CreatedAt = at, LastMessageAt = at };
                ctx.InboxThreads.Add(thread);
            }
            var msg = InboxSeed.Inbound(ws, thread.Id, at, "Re: s", "hi", "l@x.test", $"r-{Guid.NewGuid():N}");
            msg.IsAutoReply = auto;
            ctx.InboxMessages.Add(msg);
        }
    }

    private async Task Seed(Guid ws, Action<Builder> build, int steps = 2, string name = "Spring")
    {
        await using var ctx = _db.CreateContext(null);
        build(new Builder(ctx, ws).WithSteps(steps, name));
        await ctx.SaveChangesAsync();
    }

    private async Task<T> Run<T>(Guid ws, Func<AnalyticsUseCase, Task<AnalyticsOutcome<T>>> call) where T : class
    {
        await using var ctx = _db.CreateContext(ws);                            // a request in that workspace
        var outcome = await call(new AnalyticsUseCase(new AnalyticsRepository(ctx), _clock));
        Assert.Equal(AnalyticsOutcomeKind.Ok, outcome.Kind);
        return outcome.Value!;
    }

    private Task<WorkspaceAnalytics> Workspace(Guid ws, DateOnly? from = null, DateOnly? to = null)
        => Run(ws, u => u.WorkspaceAsync(ws, new AnalyticsQuery(from ?? From, to ?? To), default));

    private Task<CampaignAnalytics> Campaign(Guid ws, Guid id) => Run(ws, u => u.CampaignAsync(ws, id, new AnalyticsQuery(From, To), default));

    [Fact]
    public async Task Totals_and_rates_for_the_cohort()
    {
        await Seed(_a, b =>
        {
            b.Lead((1, Day(2), m => { m.DeliveredAt = Day(2); m.OpenedAt = Day(3); }));
            b.Lead((1, Day(2), m => m.DeliveredAt = Day(2)));
            b.Lead((1, Day(3), m => { m.Status = EmailMessageStatus.Bounced; m.BouncedAt = Day(3); }));
            b.Lead((1, Day(4), m => m.Status = EmailMessageStatus.Dropped));
        });

        var t = (await Workspace(_a)).Totals;
        Assert.Equal((4, 2, 1, 2, 4, 0), (t.Sent, t.Delivered, t.Opened, t.Bounced, t.ContactedLeads, t.RepliedLeads));
        Assert.Equal(0.5, t.DeliveredRate);
        Assert.Equal(0.5, t.OpenRate);
        Assert.Equal(0.5, t.BounceRate);
        Assert.Equal(0d, t.ReplyRate);
        Assert.True(t.OpensApproximate);
    }

    [Fact]
    public async Task Empty_range_has_null_rates()
    {
        var t = (await Workspace(_a)).Totals;
        Assert.Equal(0, t.Sent);
        Assert.Null(t.DeliveredRate);
        Assert.Null(t.OpenRate);
        Assert.Null(t.ReplyRate);
        Assert.Null(t.BounceRate);
    }

    [Fact]
    public async Task Cohort_excludes_messages_sent_before_the_range_even_if_opened_inside_it()
    {
        await Seed(_a, b =>
        {
            b.Lead((1, new DateTime(2026, 2, 27, 10, 0, 0, DateTimeKind.Utc), m => { m.DeliveredAt = Day(2); m.OpenedAt = Day(2); }));
            b.Lead((1, Day(5), null));
        });
        var w = await Workspace(_a);
        Assert.Equal((1, 0), (w.Totals.Sent, w.Totals.Opened));
        Assert.Equal(0, w.Trend.Sum(p => p.Opened));
    }

    [Fact]
    public async Task Opens_are_unique_per_message()
    {
        await Seed(_a, b => b.Lead((1, Day(2), m => { m.DeliveredAt = Day(2); m.OpenedAt = Day(2, 11); })));
        Assert.Equal(1, (await Workspace(_a)).Totals.Opened);                  // OpenedAt is set once by the rules however many open events came
    }

    [Fact]
    public async Task Replied_leads_count_distinct_enrollments_and_exclude_auto_replies_and_other_campaigns()
    {
        Guid campaignId = default;
        await Seed(_a, b =>
        {
            campaignId = b.Campaign.Id;
            var (e1, _) = b.Lead((1, Day(2), m => m.DeliveredAt = Day(2)));
            b.Reply(e1, Day(3));
            b.Reply(e1, Day(4));                                               // same lead twice → 1
            var (e2, _) = b.Lead((1, Day(2), m => m.DeliveredAt = Day(2)));
            b.Reply(e2, Day(3), auto: true);                                   // out-of-office: not a reply
            b.Lead((1, Day(2), null));
        });
        await Seed(_a, b =>
        {
            var (other, _) = b.Lead((1, Day(2), null));
            b.Reply(other, Day(3));
        }, name: "Other");

        var c = await Campaign(_a, campaignId);
        Assert.Equal((1, 3), (c.Totals.RepliedLeads, c.Totals.ContactedLeads));
        Assert.Equal(1d / 3, c.Totals.ReplyRate!.Value, 6);

        var w = await Workspace(_a);
        Assert.Equal(2, w.Totals.RepliedLeads);
        Assert.Equal(2, w.Campaigns.Count);
        Assert.Equal(1, w.Campaigns.Single(r => r.Name == "Other").Totals.RepliedLeads);
    }

    [Fact]
    public async Task Per_step_breakdown_attributes_each_reply_to_the_last_step_sent_before_it()
    {
        Guid campaignId = default;
        await Seed(_a, b =>
        {
            campaignId = b.Campaign.Id;
            var (e1, _) = b.Lead((1, Day(1), m => m.DeliveredAt = Day(1)), (2, Day(4), m => m.DeliveredAt = Day(4)));
            b.Reply(e1, Day(5));                                               // after step 2 → step 2
            var (e2, _) = b.Lead((1, Day(1), m => { m.DeliveredAt = Day(1); m.OpenedAt = Day(2); }), (2, Day(6), null));
            b.Reply(e2, Day(2));                                               // before step 2 was sent → step 1
            b.Lead((1, Day(1), m => { m.Status = EmailMessageStatus.Bounced; m.BouncedAt = Day(1); }));
        }, steps: 3);

        var c = await Campaign(_a, campaignId);
        Assert.Equal([1, 2, 3], c.Steps.Select(s => s.Order));
        var (s1, s2, s3) = (c.Steps[0], c.Steps[1], c.Steps[2]);
        Assert.Equal((3, 2, 1, 1, 1), (s1.Sent, s1.Delivered, s1.Opened, s1.Bounced, s1.Replies));
        Assert.Equal((2, 1, 0, 0, 1), (s2.Sent, s2.Delivered, s2.Opened, s2.Bounced, s2.Replies));
        Assert.Equal((0, 0), (s3.Sent, s3.Replies));                          // zero-filled
        Assert.Equal("Subject 2", s2.SubjectTemplate);
        Assert.Equal(2, c.Totals.RepliedLeads);
    }

    [Fact]
    public async Task Trend_is_zero_filled_and_buckets_by_workspace_local_day()
    {
        await Seed(_ny, b =>
        {
            b.Lead((1, new DateTime(2026, 3, 9, 3, 30, 0, DateTimeKind.Utc), null));    // 23:30 on 8 March in New York (EDT)
            b.Lead((1, new DateTime(2026, 3, 9, 4, 15, 0, DateTimeKind.Utc), m => m.OpenedAt = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc)));   // 00:15 on 9 March
        });

        var w = await Workspace(_ny, new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 11));   // contains the 2026-03-08 DST change
        Assert.Equal(7, w.Trend.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 3, 5).AddDays(i)), w.Trend.Select(p => p.Day));
        Assert.Equal(1, w.Trend.Single(p => p.Day == new DateOnly(2026, 3, 8)).Sent);
        Assert.Equal(1, w.Trend.Single(p => p.Day == new DateOnly(2026, 3, 9)).Sent);
        Assert.Equal(1, w.Trend.Single(p => p.Day == new DateOnly(2026, 3, 10)).Opened);
        Assert.Equal("America/New_York", w.TimeZone);
    }

    [Fact]
    public async Task Trend_counts_first_replies_by_day()
    {
        await Seed(_a, b =>
        {
            var (e, _) = b.Lead((1, Day(2), null));
            b.Reply(e, Day(4));
            b.Reply(e, Day(6));
        });
        var w = await Workspace(_a);
        Assert.Equal(1, w.Trend.Single(p => p.Day == new DateOnly(2026, 3, 4)).Replies);
        Assert.Equal(1, w.Trend.Sum(p => p.Replies));
    }

    [Fact]
    public async Task Workspace_B_never_sees_workspace_A_messages()
    {
        await Seed(_a, b => b.Lead((1, Day(2), m => m.DeliveredAt = Day(2))));
        var w = await Workspace(_b);
        Assert.Equal(0, w.Totals.Sent);
        Assert.Empty(w.Campaigns);
    }

    [Fact]
    public async Task Another_workspaces_campaign_is_not_found()
    {
        Guid campaignId = default;
        await Seed(_a, b => campaignId = b.Campaign.Id);
        await using var ctx = _db.CreateContext(_b);
        var outcome = await new AnalyticsUseCase(new AnalyticsRepository(ctx), _clock).CampaignAsync(_b, campaignId, new AnalyticsQuery(null, null), default);
        Assert.Equal(AnalyticsOutcomeKind.NotFound, outcome.Kind);
    }

    [Fact]
    public async Task Invalid_ranges_are_reported()
    {
        await using var ctx = _db.CreateContext(_a);
        var useCase = new AnalyticsUseCase(new AnalyticsRepository(ctx), _clock);
        Assert.Equal(AnalyticsOutcomeKind.InvalidRange, (await useCase.WorkspaceAsync(_a, new AnalyticsQuery(To, From), default)).Kind);
        Assert.Equal(AnalyticsOutcomeKind.InvalidRange, (await useCase.WorkspaceAsync(_a, new AnalyticsQuery(new DateOnly(2024, 1, 1), To), default)).Kind);
    }
}
