using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Application.Inbound;
using ZenLead.Application.UseCases.Inbound;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Inbound;

/// <summary>Marks sanitised output so tests can see the sanitizer ran without depending on its rules.</summary>
public class FakeHtmlSanitizer : IHtmlSanitizer
{
    public int Calls { get; private set; }
    public string Sanitize(string html)
    {
        Calls++;
        return "[sanitized]" + html.Length;
    }
}

public record SeededEnrollment(Guid WorkspaceId, Guid CampaignId, Guid LeadId, string LeadEmail, Guid EnrollmentId, Guid EmailMessageId)
{
    public string ReplyTo => ReplyAddress.Encode(EnrollmentId, InboundFixtures.InboundDomain);
}

/// <summary>
/// <see cref="ProcessInboundEmailUseCase"/> over the real <see cref="InboundStore"/> on in-memory SQLite. Each
/// <see cref="ProcessAsync"/> is a fresh context with no current workspace, exactly like one anonymous webhook request.
/// </summary>
public sealed class InboundHarness : IDisposable
{
    public TestDb Db { get; } = new();
    public Guid WorkspaceA { get; } = Guid.NewGuid();
    public Guid WorkspaceB { get; } = Guid.NewGuid();
    public FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 11, 6, 30, 0, TimeSpan.Zero));
    public FakeHtmlSanitizer Sanitizer { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public EmailOptions Email { get; } = new() { InboundDomain = InboundFixtures.InboundDomain, FromAddress = "outreach@zenrax.test" };

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public InboundHarness()
    {
        using var ctx = Db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceA, Name = "A", CreatedAt = DateTime.UtcNow });
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceB, Name = "B", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public async Task<InboundResult> ProcessAsync(InboundEmail mail)
    {
        await using var ctx = Db.CreateContext(null);
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        var useCase = new ProcessInboundEmailUseCase(new InboundStore(ctx, Clock), Sanitizer, Options.Create(Email),
            Options.Create(new InboundOptions()), Clock, loggerFactory.CreateLogger<ProcessInboundEmailUseCase>());
        return await useCase.ExecuteAsync(mail, CancellationToken.None);
    }

    /// <summary>A lead in a campaign with one sent step-1 email (the outbound side of the conversation).</summary>
    public async Task<SeededEnrollment> SeedAsync(Guid? workspace = null, string? email = null, LeadStatus leadStatus = LeadStatus.Contacted,
        EnrollmentStatus enrollmentStatus = EnrollmentStatus.Active, int sentDaysAgo = 1, string? providerMessageId = null)
    {
        var ws = workspace ?? WorkspaceA;
        email ??= $"lead.{Guid.NewGuid():N}@example.com";
        await using var ctx = Db.CreateContext(null);
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Jane Doe", Email = email, Status = leadStatus, CreatedAt = Now.AddDays(-30) };
        var step = new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = ws, Order = 1, SubjectTemplate = "Quick question", BodyTemplate = "Hello" };
        var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Spring", Status = CampaignStatus.Active, CreatedAt = Now.AddDays(-30), Steps = [step] };
        var enrollment = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaign.Id, LeadId = lead.Id, Status = enrollmentStatus, NextStepOrder = 2,
            NextSendAt = enrollmentStatus == EnrollmentStatus.Active ? Now.AddDays(2) : null, EnrolledAt = Now.AddDays(-10)
        };
        var sentAt = Now.AddDays(-sentDaysAgo);
        var message = new EmailMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaign.Id, EnrollmentId = enrollment.Id, StepId = step.Id, LeadId = lead.Id,
            Subject = "Quick question", Body = "Hello", Status = EmailMessageStatus.Sent, QueuedAt = sentAt, SentAt = sentAt,
            ProviderMessageId = providerMessageId ?? $"pm{Guid.NewGuid():N}"
        };
        ctx.AddRange(lead, campaign, enrollment, message);
        await ctx.SaveChangesAsync();
        return new SeededEnrollment(ws, campaign.Id, lead.Id, email, enrollment.Id, message.Id);
    }

    public static InboundEmail Mail(string from, IEnumerable<string> to, string? messageId = "msg-1@mail.example.com", string subject = "Re: Quick question",
        string? text = "Yes, let's talk.\n\nOn Tue, 10 Mar 2026 at 10:00, Zenrax <outreach@zenrax.test> wrote:\n> Hello", string? html = null,
        string? inReplyTo = null, IReadOnlyList<string>? references = null, Dictionary<string, string>? headers = null,
        string? dkim = "{@example.com : pass}", string? spf = "pass", DateTime? receivedAt = null)
        => new(from, "Jane Doe", to.ToList(), subject, text, html, messageId, inReplyTo, references ?? [],
            headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Subject"] = subject, ["Date"] = "Wed, 11 Mar 2026 10:15:02 +0400" },
            dkim, spf, receivedAt ?? new DateTime(2026, 3, 11, 6, 15, 2, DateTimeKind.Utc));

    public InboundEmail ReplyFrom(SeededEnrollment s, string? messageId = "msg-1@mail.example.com", Dictionary<string, string>? headers = null, string? from = null,
        string? dkim = "{@example.com : pass}", string? spf = "pass", string subject = "Re: Quick question")
        => Mail(from ?? s.LeadEmail, [s.ReplyTo], messageId, subject, headers: headers, dkim: dkim, spf: spf);

    // ---- reading (platform view: no tenant filter) ----

    public async Task<T> Read<T>(Func<ZenLeadDbContext, Task<T>> query)
    {
        await using var ctx = Db.CreateContext(null);
        return await query(ctx);
    }

    public Task<List<InboxThread>> Threads() => Read(c => c.InboxThreads.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    public Task<List<InboxMessage>> Messages() => Read(c => c.InboxMessages.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.OccurredAt).ToListAsync());
    public Task<List<InboundQuarantine>> Quarantine() => Read(c => c.InboundQuarantine.AsNoTracking().ToListAsync());
    public Task<Lead> Lead(Guid id) => Read(c => c.Leads.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == id));
    public Task<CampaignEnrollment> Enrollment(Guid id) => Read(c => c.CampaignEnrollments.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id));

    public void Dispose() => Db.Dispose();
}
