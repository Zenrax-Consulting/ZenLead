using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Support;

public sealed record DueSend(Workspace Workspace, Campaign Campaign, Lead Lead, CampaignEnrollment Enrollment);

/// <summary>An active campaign whose window is always open, with one allow-listed lead due for step 1 now. Reused by the job tests (T05).</summary>
public static class SendingSeed
{
    public static async Task<DueSend> DueEnrollmentAsync(ZenLeadDbContext db, DateTime nowUtc, bool aiPersonalisation = false, string? email = null)
    {
        var ws = await new WorkspaceBuilder().InTimeZone("UTC").SaveAsync(db);
        var company = await new CompanyBuilder(ws.Id).Named("Acme").InIndustry("Logistics").SaveAsync(db);
        var lead = await new LeadBuilder(ws.Id).Named("Ada Lovelace").WithEmail(email ?? $"ada-{Guid.NewGuid():N}@example.test")
            .WithTitle("CTO").AtCompany(company).SaveAsync(db);
        var campaign = await new CampaignBuilder(ws.Id)
            .Active(nowUtc.AddDays(-1))
            .WithWindow(TimeOnly.MinValue, new TimeOnly(23, 59), SendDays.All)
            .InTimeZone("UTC")
            .WithDailyCap(100)
            .WithStep(0, "Hello {{firstName}}", "Hi {{firstName}},\n\nQuick question about {{company}}.", ai: aiPersonalisation)
            .WithStep(3, "Following up", "Hi {{firstName}}, just following up.")
            .SaveAsync(db);
        var enrollment = await new EnrollmentBuilder(campaign, lead).EnrolledAt(nowUtc.AddHours(-1)).DueAt(nowUtc.AddMinutes(-1)).SaveAsync(db);
        return new DueSend(ws, campaign, lead, enrollment);
    }
}
