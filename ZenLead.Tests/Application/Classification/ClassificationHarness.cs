using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Classification;
using ZenLead.Application.UseCases.Classification;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Application.Ai;
using ZenLead.Tests.Support;
using Opts = Microsoft.Extensions.Options.Options;

namespace ZenLead.Tests.Application.Classification;

/// <summary>Scripted classifier: returns <see cref="Next"/> or throws <see cref="Throw"/>; records every input.</summary>
public class FakeReplyClassifier : IReplyClassifier
{
    public ClassificationResult Next { get; set; } = new(ReplyClassification.Interested, 0.95m, 120, 12);
    public Exception? Throw { get; set; }
    public List<ReplyClassificationInput> Inputs { get; } = [];

    public Task<ClassificationResult> ClassifyAsync(ReplyClassificationInput input, CancellationToken ct = default)
    {
        Inputs.Add(input);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Next);
    }

    public void Returns(ReplyClassification label, decimal confidence, int prompt = 120, int completion = 12)
        => Next = new ClassificationResult(label, confidence, prompt, completion);
}

/// <param name="OtherEnrollmentId">The same lead's enrollment in a second, still-running campaign.</param>
public record SeededReply(Guid WorkspaceId, Guid LeadId, string LeadEmail, Guid CampaignId, Guid EnrollmentId, Guid OtherEnrollmentId, Guid ThreadId, Guid MessageId);

/// <summary>
/// <see cref="ClassifyReplyUseCase"/> and <see cref="ClassificationEffects"/> over the real <see cref="InboxClassificationStore"/> and
/// <see cref="SuppressionList"/> on in-memory SQLite, with a fake classifier, budget and usage tracker. Each run is a fresh context with
/// no current workspace, exactly like one Hangfire job.
/// </summary>
public sealed class ClassificationHarness : IDisposable
{
    public TestDb Db { get; } = new();
    public Guid WorkspaceA { get; } = Guid.NewGuid();
    public Guid WorkspaceB { get; } = Guid.NewGuid();
    public FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 11, 6, 30, 0, TimeSpan.Zero));   // a Wednesday
    public FakeReplyClassifier Classifier { get; } = new();
    public FakeAiBudget Budget { get; } = new();
    public FakeTokenUsageTracker Usage { get; } = new();
    public AiPricing Pricing { get; } = new();
    public ClassificationOptions Options { get; } = new();
    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public ClassificationHarness()
    {
        using var ctx = Db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceA, Name = "A", CreatedAt = DateTime.UtcNow });
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceB, Name = "B", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    /// <summary>A use case + effects over a fresh context (no current workspace); the caller disposes the context.</summary>
    public (ClassifyReplyUseCase UseCase, ClassificationEffects Effects, ZenLeadDbContext Ctx) Build()
    {
        var ctx = Db.CreateContext(null);
        var store = new InboxClassificationStore(ctx, new CampaignEnrollmentRepository(ctx), NullLogger<InboxClassificationStore>.Instance);
        var effects = new ClassificationEffects(store, new SuppressionList(ctx, Clock), Opts.Create(Options), Clock);
        var useCase = new ClassifyReplyUseCase(store, Classifier, Budget, Usage, Pricing, effects, Opts.Create(Options),
            NullLogger<ClassifyReplyUseCase>.Instance);
        return (useCase, effects, ctx);
    }

    public async Task ClassifyAsync(Guid workspaceId, Guid messageId)
    {
        var (useCase, _, ctx) = Build();
        await using (ctx) await useCase.ExecuteAsync(workspaceId, messageId, CancellationToken.None);
    }

    public async Task ApplyAsync(Guid workspaceId, Guid messageId, ReplyClassification label, decimal confidence, bool manual)
    {
        var (_, effects, ctx) = Build();
        await using (ctx) await effects.ApplyAsync(workspaceId, messageId, label, confidence, manual, note: null, CancellationToken.None);
    }

    /// <summary>
    /// The state F25 leaves behind: lead Replied, the reply's enrollment Replied, a thread with one outbound and one inbound message,
    /// plus the same lead actively enrolled in a second campaign.
    /// </summary>
    public async Task<SeededReply> SeedAsync(Guid? workspace = null, string replyText = "Sounds interesting - can you send pricing?",
        bool isAutoReply = false, LeadStatus leadStatus = LeadStatus.Replied, EnrollmentStatus enrollmentStatus = EnrollmentStatus.Replied,
        ClassificationSource classifiedBy = ClassificationSource.None, ReplyClassification classification = ReplyClassification.Unclassified)
    {
        var ws = workspace ?? WorkspaceA;
        var email = $"lead.{Guid.NewGuid():N}@example.com";
        await using var ctx = Db.CreateContext(null);
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Jane Doe", Email = email, Status = leadStatus, CreatedAt = Now.AddDays(-30) };
        Campaign NewCampaign(string name) => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, Name = name, Status = CampaignStatus.Active, CreatedAt = Now.AddDays(-30),
            Steps = [new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = ws, Order = 1, SubjectTemplate = "Quick question", BodyTemplate = "Hello" },
                     new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = ws, Order = 2, DelayDays = 3, SubjectTemplate = "Following up", BodyTemplate = "Hi again" }]
        };
        var campaign = NewCampaign("Spring");
        var other = NewCampaign("Summer");
        var enrollment = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = campaign.Id, LeadId = lead.Id, Status = enrollmentStatus, NextStepOrder = 2,
            NextSendAt = enrollmentStatus == EnrollmentStatus.Active ? Now.AddDays(2) : null, EnrolledAt = Now.AddDays(-10)
        };
        var otherEnrollment = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, CampaignId = other.Id, LeadId = lead.Id, Status = EnrollmentStatus.Active, NextStepOrder = 1,
            NextSendAt = Now.AddDays(1), EnrolledAt = Now.AddDays(-2)
        };
        var thread = new InboxThread
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, LeadId = lead.Id, CampaignId = campaign.Id, EnrollmentId = enrollment.Id, Subject = "Quick question",
            LastMessageAt = Now.AddMinutes(-15), IsRead = false, Classification = classification, CreatedAt = Now.AddDays(-1)
        };
        var outbound = new InboxMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, ThreadId = thread.Id, Direction = MessageDirection.Outbound, FromAddress = "outreach@zenrax.test",
            ToAddress = email, Subject = "Quick question", TextBody = "Hello Jane, would a short call make sense?", OccurredAt = Now.AddDays(-1)
        };
        var inbound = NewInbound(ws, thread.Id, email, replyText, Now.AddMinutes(-15), isAutoReply, classifiedBy, classification);
        ctx.AddRange(lead, campaign, other, enrollment, otherEnrollment, thread, outbound, inbound);
        await ctx.SaveChangesAsync();
        return new SeededReply(ws, lead.Id, email, campaign.Id, enrollment.Id, otherEnrollment.Id, thread.Id, inbound.Id);
    }

    /// <summary>Adds another inbound message to the seeded thread.</summary>
    public async Task<Guid> AddReplyAsync(SeededReply s, string text, DateTime occurredAt, bool isAutoReply = false,
        ReplyClassification classification = ReplyClassification.Unclassified, ClassificationSource classifiedBy = ClassificationSource.None)
    {
        await using var ctx = Db.CreateContext(null);
        var m = NewInbound(s.WorkspaceId, s.ThreadId, s.LeadEmail, text, occurredAt, isAutoReply, classifiedBy, classification);
        ctx.InboxMessages.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private static InboxMessage NewInbound(Guid ws, Guid threadId, string from, string text, DateTime at, bool isAutoReply,
        ClassificationSource classifiedBy, ReplyClassification classification) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, ThreadId = threadId, Direction = MessageDirection.Inbound, FromAddress = from,
        ToAddress = "r-x@reply.zenlead.test", Subject = "Re: Quick question", TextBody = text, FullTextBody = text + "\n\n> Hello Jane",
        OccurredAt = at, IsAutoReply = isAutoReply, ClassifiedBy = classifiedBy, Classification = classification,
        RawMessageId = $"{Guid.NewGuid():N}@mail.example.com"
    };

    // ---- reading (platform view: no tenant filter) ----

    public async Task<T> Read<T>(Func<ZenLeadDbContext, Task<T>> query)
    {
        await using var ctx = Db.CreateContext(null);
        return await query(ctx);
    }

    public Task<InboxMessage> Message(Guid id) => Read(c => c.InboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id));
    public Task<InboxThread> Thread(Guid id) => Read(c => c.InboxThreads.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == id));
    public Task<Lead> Lead(Guid id) => Read(c => c.Leads.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == id));
    public Task<CampaignEnrollment> Enrollment(Guid id) => Read(c => c.CampaignEnrollments.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id));
    public Task<List<SuppressedEmail>> Suppressions() => Read(c => c.SuppressedEmails.IgnoreQueryFilters().AsNoTracking().ToListAsync());

    public void Dispose() => Db.Dispose();
}
