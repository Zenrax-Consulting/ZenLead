using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenLead.Application.Email;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Campaigns;

/// <summary>
/// Campaign use cases over the real EF repositories on in-memory SQLite (tenant filter included). Every <see cref="Run{T}"/> is
/// a fresh context, like a separate request.
/// </summary>
public sealed class CampaignHarness : IDisposable
{
    public TestDb Db { get; } = new();
    public Guid WorkspaceA { get; } = Guid.NewGuid();
    public Guid WorkspaceB { get; } = Guid.NewGuid();
    public Guid UserId { get; } = Guid.NewGuid();

    /// <summary>Wednesday 2026-03-11 06:00 UTC = 10:00 in Asia/Dubai (workspace A's zone).</summary>
    public FixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 11, 6, 0, 0, TimeSpan.Zero));

    public EmailOptions Email { get; } = new() { DomainVerified = true };

    public CampaignHarness()
    {
        using var ctx = Db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceA, Name = "A", TimeZone = "Asia/Dubai", CreatedAt = DateTime.UtcNow });
        ctx.Workspaces.Add(new Workspace { Id = WorkspaceB, Name = "B", TimeZone = "UTC", CreatedAt = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    public sealed class Scope(ZenLeadDbContext db, CampaignHarness h)
    {
        public ZenLeadDbContext Db { get; } = db;
        public CampaignRepository Campaigns { get; } = new(db);
        public CampaignEnrollmentRepository Enrollments { get; } = new(db);
        public LeadRepository Leads { get; } = new(db);
        public WorkspaceRepository Workspaces { get; } = new(db);

        public CampaignsUseCase Manage => new(Campaigns, Enrollments, Leads, Workspaces, h.Clock);
        public CampaignStepsUseCase Steps => new(Campaigns, Enrollments, new EfUnitOfWork(Db));
        public SuppressionList Suppression { get; } = new(db, h.Clock);
        public EnrollLeadsUseCase Enroll => new(Campaigns, Enrollments, Leads, Workspaces, Suppression, h.Clock);
        public ActivateCampaignUseCase Activate => new(Campaigns, Enrollments, Workspaces, new EfUnitOfWork(Db), Options.Create(h.Email), h.Clock);
        public PauseCampaignUseCase Pause => new(Campaigns);
    }

    public async Task<T> Run<T>(Func<Scope, Task<T>> action, Guid? workspace = null)
    {
        await using var ctx = Db.CreateContext(workspace ?? WorkspaceA);
        return await action(new Scope(ctx, this));
    }

    public async Task<T> Read<T>(Func<ZenLeadDbContext, Task<T>> query)
    {
        await using var ctx = Db.CreateContext(null);
        return await query(ctx);
    }

    public async Task<Lead> AddLeadAsync(string name, Guid? workspace = null, LeadStatus status = LeadStatus.New,
        EmailVerificationStatus verification = EmailVerificationStatus.Verified, string? company = "Acme")
    {
        await using var ctx = Db.CreateContext(null);
        var ws = workspace ?? WorkspaceA;
        Company? co = null;
        if (company is not null)
        {
            co = new Company { Id = Guid.NewGuid(), WorkspaceId = ws, Name = company, CreatedAt = DateTime.UtcNow };
            ctx.Companies.Add(co);
        }
        var lead = new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, Name = name, Email = $"{Guid.NewGuid():N}@example.com", Status = status,
            EmailVerificationStatus = verification, CompanyId = co?.Id, CreatedAt = DateTime.UtcNow
        };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();
        return lead;
    }

    /// <summary>A campaign with the given steps (subject/body templates), straight into the database.</summary>
    public async Task<Campaign> AddCampaignAsync(CampaignStatus status = CampaignStatus.Draft, Guid? workspace = null,
        params (int Delay, string Subject, string Body)[] steps)
    {
        await using var ctx = Db.CreateContext(null);
        var ws = workspace ?? WorkspaceA;
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Spring outreach", Status = status, CreatedBy = UserId, CreatedAt = DateTime.UtcNow,
            Steps = steps.Select((s, i) => new CampaignStep
            {
                Id = Guid.NewGuid(), WorkspaceId = ws, Order = i + 1, DelayDays = s.Delay, SubjectTemplate = s.Subject, BodyTemplate = s.Body
            }).ToList()
        };
        ctx.Campaigns.Add(campaign);
        await ctx.SaveChangesAsync();
        return campaign;
    }

    public static (int, string, string) Step(int delay = 0, string subject = "Hi {{firstName|there}}", string body = "Hello {{firstName|there}}")
        => (delay, subject, body);

    public async Task<CampaignEnrollment> AddEnrollmentAsync(Campaign campaign, Lead lead, int nextStepOrder = 1, DateTime? nextSendAt = null,
        EnrollmentStatus status = EnrollmentStatus.Active)
    {
        await using var ctx = Db.CreateContext(null);
        var e = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = campaign.WorkspaceId, CampaignId = campaign.Id, LeadId = lead.Id,
            NextStepOrder = nextStepOrder, NextSendAt = nextSendAt, Status = status, EnrolledAt = DateTime.UtcNow
        };
        ctx.CampaignEnrollments.Add(e);
        await ctx.SaveChangesAsync();
        return e;
    }

    public Task<List<CampaignEnrollment>> EnrollmentsOf(Guid campaignId)
        => Read(ctx => ctx.CampaignEnrollments.IgnoreQueryFilters().Where(e => e.CampaignId == campaignId).ToListAsync());

    public Task<List<CampaignStep>> StepsOf(Guid campaignId)
        => Read(ctx => ctx.CampaignSteps.IgnoreQueryFilters().Where(s => s.CampaignId == campaignId).OrderBy(s => s.Order).ToListAsync());

    public Task<Campaign> CampaignById(Guid id)
        => Read(ctx => ctx.Campaigns.IgnoreQueryFilters().SingleAsync(c => c.Id == id));

    public void Dispose() => Db.Dispose();
}
