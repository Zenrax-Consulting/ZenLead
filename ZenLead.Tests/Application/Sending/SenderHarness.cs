using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZenLead.Application;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.Infrastructure.Ai;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Infrastructure.Security;
using ZenLead.Tests.Application.Campaigns;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Sending;

/// <summary>A sender whose answer (or exception) per email the test programs. Records every call, including ones that throw.</summary>
public class ProgrammableSender : IEmailSender
{
    public List<OutboundEmail> Calls { get; } = [];
    public Func<OutboundEmail, Task<SendResult>> Handler { get; set; } = _ => Task.FromResult(SendResult.Ok("sg-123"));

    public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        Calls.Add(email);
        return Handler(email);
    }
}

public class ProgrammableComposer : IEmailComposer
{
    public int Calls { get; private set; }
    public EmailComposeContext? LastContext { get; private set; }
    public Func<EmailComposeContext, ComposedEmail> Handler { get; set; } = _ => new ComposedEmail("AI subject", "AI body", 150, 100, 50);

    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        Calls++;
        LastContext = context;
        return Task.FromResult(Handler(context));
    }
}

/// <summary>Delegates to the real store; lets a test inject a crash on save or hide existing messages (race window).</summary>
public class InterceptingStore(ISenderStore inner) : ISenderStore
{
    public int FailNextSaves { get; set; }
    public bool HideExistingMessages { get; set; }

    public Task<IReadOnlyList<DueEnrollment>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct) => inner.GetDueAsync(nowUtc, limit, ct);
    public Task<SendContext?> LoadAsync(Guid workspaceId, Guid enrollmentId, CancellationToken ct) => inner.LoadAsync(workspaceId, enrollmentId, ct);
    public Task<int> CountMessagesSinceAsync(Guid workspaceId, Guid? campaignId, DateTime sinceUtc, CancellationToken ct) => inner.CountMessagesSinceAsync(workspaceId, campaignId, sinceUtc, ct);
    public Task<EmailMessage?> FindMessageAsync(Guid enrollmentId, Guid stepId, CancellationToken ct)
        => HideExistingMessages ? Task.FromResult<EmailMessage?>(null) : inner.FindMessageAsync(enrollmentId, stepId, ct);
    public Task<bool> TryInsertQueuedAsync(EmailMessage message, CancellationToken ct) => inner.TryInsertQueuedAsync(message, ct);
    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct) => inner.DeleteMessageAsync(messageId, ct);
    public Task SaveAsync(CancellationToken ct)
    {
        if (FailNextSaves > 0)
        {
            FailNextSaves--;
            throw new InvalidOperationException("simulated crash before save");     // nothing persisted; the run's context is then dropped
        }
        return inner.SaveAsync(ct);
    }
    public Task<Campaign?> GetCampaignAsync(Guid workspaceId, Guid campaignId, CancellationToken ct) => inner.GetCampaignAsync(workspaceId, campaignId, ct);
    public Task<bool> CampaignHasLiveEnrollmentsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct) => inner.CampaignHasLiveEnrollmentsAsync(workspaceId, campaignId, ct);
    public Task AttachOutboundAsync(SendContext ctx, EmailMessage message, string fromAddress, CancellationToken ct) => inner.AttachOutboundAsync(ctx, message, fromAddress, ct);
}

/// <summary>
/// The sender over the real EF stores on in-memory SQLite (the same data <see cref="CampaignHarness"/> seeds). Each
/// <see cref="RunAsync"/> is a fresh DbContext with no current workspace, exactly like one Hangfire job execution.
/// Clock: Wednesday 2026-03-11 06:00 UTC = 10:00 Asia/Dubai (workspace A).
/// </summary>
public sealed class SenderHarness : IDisposable
{
    public CampaignHarness Campaigns { get; } = new();
    public FixedTimeProvider Clock => Campaigns.Clock;
    public Guid WorkspaceA => Campaigns.WorkspaceA;
    public Guid WorkspaceB => Campaigns.WorkspaceB;
    public Guid WorkspaceNy { get; } = Guid.NewGuid();

    public ProgrammableSender Sender { get; } = new();
    public ProgrammableComposer Composer { get; } = new();
    public FakeAiBudget Budget { get; } = new();
    public AiPricing Pricing { get; } = new();
    public SendingOptions Sending { get; } = new() { RecipientAllowList = ["example.com"] };
    public EmailOptions Email { get; } = new() { FromAddress = "outreach@zenrax.test", FromName = "Zenrax", InboundDomain = "reply.zenrax.test" };
    public AppOptions App { get; } = new() { PublicBaseUrl = "https://app.zenlead.test/" };
    public HmacUnsubscribeTokens Tokens { get; } = new(Options.Create(new UnsubscribeOptions { SigningKey = new string('u', 48) }));
    public CapturingLoggerProvider Logs { get; } = new();

    public SenderHarness()
    {
        using var ctx = Campaigns.Db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceNy, Name = "NY", TimeZone = "America/New_York", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;
    public void Advance(TimeSpan by) => Clock.Now += by;
    public void SetNow(DateTime utc) => Clock.Now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public async Task<SendRunSummary> RunAsync(Func<ISenderStore, ISenderStore>? wrap = null, CancellationToken ct = default)
    {
        await using var ctx = Campaigns.Db.CreateContext(null);
        return await UseCase(ctx, wrap).ExecuteAsync(ct);
    }

    public SendDueEmailsUseCase UseCase(ZenLeadDbContext ctx, Func<ISenderStore, ISenderStore>? wrap = null)
    {
        ISenderStore store = new SenderStore(ctx);
        if (wrap is not null) store = wrap(store);
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        return new SendDueEmailsUseCase(store, new SuppressionList(ctx, Clock), Sender, Composer, Budget,
            new EfTokenUsageTracker(ctx, NullLogger<EfTokenUsageTracker>.Instance), Pricing, Tokens,
            Options.Create(Sending), Options.Create(Email), Options.Create(App), Clock, loggerFactory.CreateLogger<SendDueEmailsUseCase>());
    }

    // ---- seeding ----

    public async Task<Campaign> ActiveCampaignAsync(Guid? workspace = null, int dailyCap = 30, SendDays days = SendDays.Weekdays,
        bool ai = false, params (int Delay, string Subject, string Body)[] steps)
    {
        if (steps.Length == 0) steps = [CampaignHarness.Step()];
        var campaign = await Campaigns.AddCampaignAsync(CampaignStatus.Active, workspace ?? WorkspaceA, steps);
        await Campaigns.Read(async ctx =>
        {
            var c = await ctx.Campaigns.IgnoreQueryFilters().Include(x => x.Steps).SingleAsync(x => x.Id == campaign.Id);
            c.DailySendCap = dailyCap;
            c.SendDays = days;
            foreach (var s in c.Steps) s.UseAiPersonalisation = ai;
            await ctx.SaveChangesAsync();
            return 0;
        });
        return await CampaignWithSteps(campaign.Id);
    }

    public Task<Lead> LeadAsync(string name = "Jane Doe", Guid? workspace = null, LeadStatus status = LeadStatus.New,
        EmailVerificationStatus verification = EmailVerificationStatus.Verified, string? company = "Acme")
        => Campaigns.AddLeadAsync(name, workspace ?? WorkspaceA, status, verification, company);

    /// <summary>An enrollment due now (NextSendAt one minute ago) unless told otherwise.</summary>
    public Task<CampaignEnrollment> EnrollAsync(Campaign campaign, Lead lead, DateTime? nextSendAt = null, int nextStepOrder = 1,
        EnrollmentStatus status = EnrollmentStatus.Active)
        => Campaigns.AddEnrollmentAsync(campaign, lead, nextStepOrder, nextSendAt ?? Now.AddMinutes(-1), status);

    public async Task<(Campaign Campaign, Lead Lead, CampaignEnrollment Enrollment)> DueAsync(Guid? workspace = null,
        params (int Delay, string Subject, string Body)[] steps)
    {
        var campaign = await ActiveCampaignAsync(workspace, steps: steps);
        var lead = await LeadAsync(workspace: workspace);
        return (campaign, lead, await EnrollAsync(campaign, lead));
    }

    public async Task AddMessageAsync(CampaignEnrollment e, Guid stepId, EmailMessageStatus status, DateTime queuedAt, DateTime? sentAt = null)
    {
        await using var ctx = Campaigns.Db.CreateContext(null);
        ctx.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = e.WorkspaceId, CampaignId = e.CampaignId, EnrollmentId = e.Id, StepId = stepId, LeadId = e.LeadId,
            Subject = "s", Body = "b", Status = status, QueuedAt = queuedAt, SentAt = sentAt
        });
        await ctx.SaveChangesAsync();
    }

    public async Task UpdateLeadAsync(Guid leadId, Action<Lead> change)
    {
        await using var ctx = Campaigns.Db.CreateContext(null);
        var lead = await ctx.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == leadId);
        change(lead);
        await ctx.SaveChangesAsync();
    }

    public async Task SuppressAsync(Guid workspaceId, string email)
    {
        await using var ctx = Campaigns.Db.CreateContext(null);
        await new SuppressionList(ctx, Clock).AddAsync(workspaceId, email, SuppressionReason.Manual, null, CancellationToken.None);
    }

    // ---- reading ----

    public Task<CampaignEnrollment> Enrollment(Guid id)
        => Campaigns.Read(ctx => ctx.CampaignEnrollments.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id));

    public Task<Lead> Lead(Guid id) => Campaigns.Read(ctx => ctx.Leads.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == id));

    public Task<List<EmailMessage>> Messages(Guid? enrollmentId = null)
        => Campaigns.Read(ctx => ctx.EmailMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => enrollmentId == null || m.EnrollmentId == enrollmentId).OrderBy(m => m.QueuedAt).ToListAsync());

    public Task<List<AiUsageLog>> UsageRows() => Campaigns.Read(ctx => ctx.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    public Task<Campaign> CampaignWithSteps(Guid id)
        => Campaigns.Read(ctx => ctx.Campaigns.IgnoreQueryFilters().AsNoTracking().Include(c => c.Steps).SingleAsync(c => c.Id == id));

    public async Task<SendWindow> WindowOf(Campaign campaign)
    {
        var tz = await Campaigns.Read(ctx => ctx.Workspaces.Where(w => w.Id == campaign.WorkspaceId).Select(w => w.TimeZone).SingleAsync());
        return CampaignWindowFactory.For(campaign, tz);
    }

    public void Dispose() => Campaigns.Dispose();
}
