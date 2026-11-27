using System.Text.Json;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Testing;

/// <summary>
/// Seed data for the E2E environment (T00). Every call creates fresh ids, and workspace names and emails carry a run id, so parallel
/// tests never collide and no reset is needed between them. Content is the same on every call: names, statuses, sources, step text,
/// and dates relative to the current <see cref="TimeProvider"/> time. Generated passwords are returned so tests can sign in.
/// Rows carry their WorkspaceId explicitly (there is no current workspace here).
/// </summary>
public class SeedScenarios(
    ZenLeadDbContext db, IIdentityService identity, SuperAdminSeeder superAdminSeeder, IOptions<SuperAdminOptions> superAdmin, TimeProvider clock)
{
    public static readonly IReadOnlyList<string> Names = ["empty", "gate2", "inbox", "analytics"];

    /// <summary>Time zone of the main E2E workspace: it has DST, so send windows and analytics buckets can be tested across a change.</summary>
    public const string MainTimeZone = "Europe/London";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<SeedResult> RunAsync(string scenario, CancellationToken ct = default)
    {
        var name = scenario.Trim().ToLowerInvariant();
        if (!Names.Contains(name))
            throw new ArgumentException($"Unknown seed scenario '{scenario}'. Known: {string.Join(", ", Names)}.", nameof(scenario));

        var result = new SeedResult { Scenario = name, RunId = Guid.NewGuid().ToString("N")[..8], Now = clock.GetUtcNow() };
        result.SuperAdminEmail = await SeedSuperAdminAsync(ct);
        if (name == "empty") return result;

        await SeedGate2Async(result, ct);
        if (name == "gate2") return result;

        await SeedInboxAsync(result, ct);
        if (name == "inbox") return result;

        await SeedAnalyticsAsync(result, ct);
        return result;
    }

    private async Task<string> SeedSuperAdminAsync(CancellationToken ct)
    {
        var email = superAdmin.Value.Email;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(superAdmin.Value.Password))
            throw new InvalidOperationException("SuperAdmin:Email and SuperAdmin:Password must be set (the E2E runner passes SuperAdmin__Email / SuperAdmin__Password).");
        await superAdminSeeder.SeedAsync(isProduction: false, ct);   // idempotent
        return email.Trim();
    }

    // ---------- gate2: two workspaces, users, companies, leads, a target profile, a draft campaign ----------

    private async Task SeedGate2Async(SeedResult r, CancellationToken ct)
    {
        var now = Now;
        var main = await WorkspaceAsync(r, "main", $"Zenrax E2E {r.RunId}", MainTimeZone, $"admin-{r.RunId}@example.test", "E2E Admin", ct);
        var other = await WorkspaceAsync(r, "other", $"Other Co {r.RunId}", "UTC", $"other-{r.RunId}@example.test", "Other User", ct);
        r.WorkspaceId = main.Id;
        r.OtherWorkspaceId = other.Id;

        string[][] companies =
        [
            ["Acme", "acme", "Software", "United Kingdom", "51-200"],
            ["Globex", "globex", "Manufacturing", "Germany", "201-500"],
            ["Initech", "initech", "Financial Services", "United States", "11-50"]
        ];
        foreach (var c in companies)
        {
            var company = new Company
            {
                Id = Guid.NewGuid(), WorkspaceId = main.Id, Name = c[0], Domain = $"{c[1]}-{r.RunId}.example.test",
                Industry = c[2], Country = c[3], Size = c[4], CreatedAt = now.AddDays(-20)
            };
            db.Companies.Add(company);
            r.CompanyIds.Add(company.Id);
        }

        string[] first = ["Alice", "Ben", "Chloe", "Dev", "Ema", "Femi", "Grace", "Hugo", "Ines", "Jon", "Kira", "Liam", "Maya",
                          "Nate", "Olga", "Pia", "Quinn", "Raj", "Sara", "Tom", "Uma", "Vik", "Wen", "Xavi", "Yara"];
        string[] titles = ["Head of Sales", "CTO", "Marketing Manager", "Operations Director", "Founder"];
        for (var i = 0; i < first.Length; i++)
        {
            var lead = new Lead
            {
                Id = Guid.NewGuid(), WorkspaceId = main.Id, Name = $"{first[i]} Tester",
                Email = $"{first[i].ToLowerInvariant()}-{r.RunId}@example.test", Title = titles[i % titles.Length],
                CompanyId = r.CompanyIds[i % r.CompanyIds.Count],
                // mostly New (enrollable), a few of every other status
                Status = (i % 8) switch { 5 => LeadStatus.Contacted, 6 => LeadStatus.Replied, 7 => i == 23 ? LeadStatus.Bounced : LeadStatus.Unsubscribed, _ => LeadStatus.New },
                Source = (i % 3) switch { 0 => LeadSource.Manual, 1 => LeadSource.Csv, _ => LeadSource.Discovery },
                CreatedAt = now.AddDays(-20).AddMinutes(i)
            };
            db.Leads.Add(lead);
            r.LeadIds.Add(lead.Id);
        }

        // one lead in the other workspace, for isolation checks
        db.Leads.Add(new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = other.Id, Name = "Outsider Lead", Email = $"outsider-{r.RunId}@example.test",
            Status = LeadStatus.New, CreatedAt = now.AddDays(-5)
        });

        var profile = new TargetProfile
        {
            Id = Guid.NewGuid(), WorkspaceId = main.Id, Name = "UK software leaders",
            CriteriaJson = JsonSerializer.Serialize(new LeadSearchCriteria(["Head of Sales", "CTO"], ["Software"], ["United Kingdom"], 50, 500, []), Web),
            CreatedBy = main.AdminUserId, CreatedAt = now.AddDays(-10)
        };
        db.TargetProfiles.Add(profile);
        r.TargetProfileId = profile.Id;

        var draft = Campaign(main.Id, main.AdminUserId, "Draft outreach", CampaignStatus.Draft, now.AddDays(-2));
        draft.Steps.AddRange(Steps(main.Id, draft.Id));
        db.Campaigns.Add(draft);
        r.DraftCampaignId = draft.Id;

        await db.SaveChangesAsync(ct);
    }

    // ---------- inbox: an active campaign with sent messages, three replied threads, one quarantined inbound ----------

    private async Task SeedInboxAsync(SeedResult r, CancellationToken ct)
    {
        var now = Now;
        var ws = r.WorkspaceId!.Value;
        var campaign = Campaign(ws, r.Workspaces["main"].AdminUserId, "Inbox outreach", CampaignStatus.Active, now.AddDays(-6));
        campaign.ActivatedAt = now.AddDays(-5);
        campaign.Steps.AddRange(Steps(ws, campaign.Id));
        db.Campaigns.Add(campaign);
        r.InboxCampaignId = campaign.Id;
        var step1 = campaign.Steps[0];

        (string Key, string Reply, ReplyClassification Label, decimal Confidence, bool NeedsReview, EnrollmentStatus Enrollment, LeadStatus Lead)[] replies =
        [
            ("interested", "Sounds interesting, can we talk on Thursday?", ReplyClassification.Interested, 0.95m, false, EnrollmentStatus.Replied, LeadStatus.Replied),
            ("unsubscribe", "Please remove me from your list.", ReplyClassification.Unsubscribe, 0.99m, false, EnrollmentStatus.Unsubscribed, LeadStatus.Unsubscribed),
            ("needsReview", "Not sure this is for us, maybe stop for now?", ReplyClassification.Unsubscribe, 0.50m, true, EnrollmentStatus.Replied, LeadStatus.Replied)
        ];

        // the first five New leads: three replied, two still waiting for step 2
        var leads = db.Leads.Local.Where(l => l.WorkspaceId == ws && l.Status == LeadStatus.New).Take(5).ToList();
        for (var i = 0; i < leads.Count; i++)
        {
            var lead = leads[i];
            var sentAt = now.AddDays(-4).AddHours(i);
            var replied = i < replies.Length;
            var enrollment = new CampaignEnrollment
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaign.Id, LeadId = lead.Id, EnrolledAt = now.AddDays(-5),
                NextStepOrder = 2, LastSentAt = sentAt,
                Status = replied ? replies[i].Enrollment : EnrollmentStatus.Active,
                NextSendAt = replied ? null : sentAt.AddDays(step1.DelayDays + 3)
            };
            db.CampaignEnrollments.Add(enrollment);
            var message = SentMessage(ws, campaign.Id, enrollment.Id, step1, lead.Id, sentAt, delivered: true, opened: replied, bounced: false);
            db.EmailMessages.Add(message);

            var thread = new InboxThread
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, LeadId = lead.Id, CampaignId = campaign.Id, EnrollmentId = enrollment.Id,
                Subject = message.Subject, LastMessageAt = replied ? sentAt.AddHours(2) : sentAt, IsRead = !replied,
                Classification = replied ? replies[i].Label : ReplyClassification.Unclassified, CreatedAt = sentAt
            };
            db.InboxThreads.Add(thread);
            db.InboxMessages.Add(new InboxMessage
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, ThreadId = thread.Id, Direction = MessageDirection.Outbound,
                FromAddress = "business@zenraxconsulting.com", ToAddress = lead.Email, Subject = message.Subject, TextBody = message.Body,
                OccurredAt = sentAt, EmailMessageId = message.Id, RawMessageId = $"sent-{message.Id:N}@example.test"
            });

            if (!replied) { r.SentEnrollmentIds.Add(enrollment.Id); continue; }

            var reply = replies[i];
            lead.Status = reply.Lead;
            db.InboxMessages.Add(new InboxMessage
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, ThreadId = thread.Id, Direction = MessageDirection.Inbound,
                FromAddress = lead.Email, FromName = lead.Name, ToAddress = $"r-{enrollment.Id:N}@reply.example.test",
                Subject = $"Re: {message.Subject}", TextBody = reply.Reply, FullTextBody = reply.Reply, OccurredAt = sentAt.AddHours(2),
                Classification = reply.Label, ClassificationConfidence = reply.Confidence, ClassifiedBy = ClassificationSource.Ai,
                NeedsReview = reply.NeedsReview, ClassificationNote = reply.NeedsReview ? "low_confidence" : null,
                RawMessageId = $"reply-{Guid.NewGuid():N}@example.test", AuthSummary = "dkim=pass; spf=pass"
            });
            if (reply.Label == ReplyClassification.Unsubscribe && !reply.NeedsReview)
                db.SuppressedEmails.Add(new SuppressedEmail { Id = Guid.NewGuid(), WorkspaceId = ws, Email = lead.Email, Reason = SuppressionReason.Unsubscribed, CreatedAt = sentAt.AddHours(2) });
            r.ThreadIds[reply.Key] = thread.Id;
        }

        var quarantined = new InboundQuarantine
        {
            Id = Guid.NewGuid(), ReceivedAt = now.AddDays(-1), Reason = InboundQuarantine.NoMatch,
            FromAddress = $"stranger-{r.RunId}@example.test", ToAddress = "r-unknown@reply.example.test", Subject = "Who is this?",
            HeadersSnippet = "From: stranger@example.test\r\nSubject: Who is this?"
        };
        db.InboundQuarantine.Add(quarantined);
        r.QuarantineId = quarantined.Id;

        await db.SaveChangesAsync(ct);
    }

    // ---------- analytics: a campaign with sends, deliveries, opens and bounces spread over the last 30 days ----------

    private async Task SeedAnalyticsAsync(SeedResult r, CancellationToken ct)
    {
        var now = Now;
        var ws = r.WorkspaceId!.Value;
        var campaign = Campaign(ws, r.Workspaces["main"].AdminUserId, "Analytics outreach", CampaignStatus.Active, now.AddDays(-32));
        campaign.ActivatedAt = now.AddDays(-31);
        campaign.Steps.AddRange(Steps(ws, campaign.Id));
        db.Campaigns.Add(campaign);
        r.AnalyticsCampaignId = campaign.Id;

        var leads = db.Leads.Local.Where(l => l.WorkspaceId == ws).OrderBy(l => l.CreatedAt).Take(20).ToList();
        for (var i = 0; i < leads.Count; i++)
        {
            var sentAt = now.AddDays(-30).AddHours(i * 36);          // every 1.5 days across the 30-day window
            var bounced = i % 10 == 9;                                 // 2 bounces
            var opened = !bounced && i % 2 == 0;                       // 10 opens
            var enrollment = new CampaignEnrollment
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaign.Id, LeadId = leads[i].Id, EnrolledAt = now.AddDays(-31),
                NextStepOrder = 2, LastSentAt = sentAt, Status = bounced ? EnrollmentStatus.Bounced : EnrollmentStatus.Completed
            };
            db.CampaignEnrollments.Add(enrollment);
            db.EmailMessages.Add(SentMessage(ws, campaign.Id, enrollment.Id, campaign.Steps[0], leads[i].Id, sentAt, delivered: !bounced, opened, bounced));
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(MainTimeZone);
        r.DstChangeInAnalyticsRange = zone.GetUtcOffset(now.AddDays(-30)) != zone.GetUtcOffset(now);
        await db.SaveChangesAsync(ct);
    }

    // ---------- helpers ----------

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string NewPassword() => $"Zl!{Guid.NewGuid():N}A1";

    private async Task<SeedWorkspace> WorkspaceAsync(SeedResult r, string key, string name, string timeZone, string adminEmail, string displayName, CancellationToken ct)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, TimeZone = timeZone, AdminEmail = adminEmail, CreatedAt = Now.AddDays(-30) };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        var password = NewPassword();
        var userId = await identity.CreateUserAsync(workspace.Id, adminEmail, password, displayName, AppUserStatus.Active, ct);
        var seeded = new SeedWorkspace(workspace.Id, name, timeZone, userId, adminEmail, password);
        r.Workspaces[key] = seeded;
        return seeded;
    }

    private static Campaign Campaign(Guid ws, Guid createdBy, string name, CampaignStatus status, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, Name = name, Status = status, CreatedBy = createdBy, CreatedAt = createdAt,
        DailySendCap = 30, WindowStart = new TimeOnly(9, 0), WindowEnd = new TimeOnly(17, 0), SendDays = SendDays.Weekdays
    };

    private static IEnumerable<CampaignStep> Steps(Guid ws, Guid campaignId) =>
    [
        new() { Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaignId, Order = 1, DelayDays = 0,
                SubjectTemplate = "Quick question for {{company}}", BodyTemplate = "Hi {{first_name}},\n\nWould a short call about {{company}} be useful?" },
        new() { Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaignId, Order = 2, DelayDays = 3,
                SubjectTemplate = "Following up", BodyTemplate = "Hi {{first_name}},\n\nJust bringing this back to the top of your inbox." },
        new() { Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaignId, Order = 3, DelayDays = 4,
                SubjectTemplate = "Last note", BodyTemplate = "Hi {{first_name}},\n\nI'll leave it here. Reply any time if it becomes relevant." }
    ];

    private static EmailMessage SentMessage(Guid ws, Guid campaignId, Guid enrollmentId, CampaignStep step, Guid leadId, DateTime sentAt,
        bool delivered, bool opened, bool bounced) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaignId, EnrollmentId = enrollmentId, StepId = step.Id, LeadId = leadId,
        ProviderMessageId = $"pickup-{Guid.NewGuid()}", Subject = "Quick question", Body = "Hi,\n\nWould a short call be useful?",
        Status = bounced ? EmailMessageStatus.Bounced : delivered ? EmailMessageStatus.Delivered : EmailMessageStatus.Sent,
        QueuedAt = sentAt.AddSeconds(-5), SentAt = sentAt,
        DeliveredAt = delivered ? sentAt.AddMinutes(1) : null,
        OpenedAt = opened ? sentAt.AddHours(1) : null,
        BouncedAt = bounced ? sentAt.AddMinutes(1) : null
    };
}

public sealed record SeedWorkspace(Guid Id, string Name, string TimeZone, Guid AdminUserId, string AdminEmail, string AdminPassword);

/// <summary>What a scenario created, returned as JSON so tests never have to query for ids.</summary>
public sealed class SeedResult
{
    public string Scenario { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTimeOffset Now { get; init; }
    public string SuperAdminEmail { get; set; } = "";
    /// <summary>"main" (Zenrax E2E, Europe/London) and "other" (Other Co, UTC; for isolation checks).</summary>
    public Dictionary<string, SeedWorkspace> Workspaces { get; } = [];
    public Guid? WorkspaceId { get; set; }
    public Guid? OtherWorkspaceId { get; set; }
    public List<Guid> CompanyIds { get; } = [];
    public List<Guid> LeadIds { get; } = [];
    public Guid? TargetProfileId { get; set; }
    public Guid? DraftCampaignId { get; set; }
    public Guid? InboxCampaignId { get; set; }
    /// <summary>"interested", "unsubscribe", "needsReview".</summary>
    public Dictionary<string, Guid> ThreadIds { get; } = [];
    /// <summary>Inbox campaign enrollments that got step 1 and are waiting for step 2.</summary>
    public List<Guid> SentEnrollmentIds { get; } = [];
    public Guid? QuarantineId { get; set; }
    public Guid? AnalyticsCampaignId { get; set; }
    /// <summary>True when the 30-day analytics window crosses a DST change in Europe/London (set the test clock first to force it).</summary>
    public bool? DstChangeInAnalyticsRange { get; set; }
}
