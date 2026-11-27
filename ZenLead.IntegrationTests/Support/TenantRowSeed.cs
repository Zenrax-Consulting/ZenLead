using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// One row of every <see cref="ITenantEntity"/> type in a workspace, plus a soft-deleted lead, and the provider-independent checks
/// that the tenant and soft-delete query filters hide what they should. Written against a context factory so T10 can run the same
/// checks on SQLite (<c>TestDb.CreateContext</c>) and prove both providers agree.
/// </summary>
public static class TenantRowSeed
{
    /// <summary>Every entity type in the model that carries a WorkspaceId tenant filter.</summary>
    public static IReadOnlyList<Type> TenantEntityTypes(ZenLeadDbContext db)
        => db.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(ITenantEntity).IsAssignableFrom(t)).OrderBy(t => t.Name).ToList();

    /// <summary>Adds the workspace and one row per tenant entity type, saved through a context with no current workspace.</summary>
    public static async Task<Workspace> SeedWorkspaceAsync(ZenLeadDbContext db, string label)
    {
        var ws = await new WorkspaceBuilder().Named($"{label} {Guid.NewGuid():N}"[..20]).SaveAsync(db);
        var company = await new CompanyBuilder(ws.Id).Named($"{label} Co").SaveAsync(db);
        var lead = await new LeadBuilder(ws.Id).AtCompany(company).SaveAsync(db);
        await new LeadBuilder(ws.Id).Named("Deleted lead").Deleted(DateTime.UtcNow).SaveAsync(db);
        var campaign = await new CampaignBuilder(ws.Id).SaveAsync(db);
        var enrollment = await new EnrollmentBuilder(campaign, lead).SaveAsync(db);
        await new EmailMessageBuilder(enrollment, campaign.Steps[0]).SaveAsync(db);
        await new InboxThreadBuilder(lead, enrollment).WithReply("Sounds good").SaveAsync(db);

        var now = DateTime.UtcNow;
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = ws.Id, Name = $"{label} profile", CreatedAt = now };
        db.TargetProfiles.Add(profile);
        db.DiscoveryRuns.Add(new LeadDiscoveryRun
        {
            Id = Guid.NewGuid(), WorkspaceId = ws.Id, TargetProfileId = profile.Id, Provider = "Fake", RequestedCount = 10,
            Status = DiscoveryRunStatus.Completed, CreatedAt = now
        });
        db.CsvImportBatches.Add(new CsvImportBatch
        {
            Id = Guid.NewGuid(), WorkspaceId = ws.Id, FileName = "leads.csv", BlobPath = $"imports/{ws.Id}/x.csv", Status = CsvImportStatus.Completed, CreatedAt = now
        });
        db.AiUsageLogs.Add(new AiUsageLog { Id = Guid.NewGuid(), WorkspaceId = ws.Id, Model = "gpt-4o", PromptTokens = 10, CompletionTokens = 5, CreatedAt = now });
        db.SuppressedEmails.Add(new SuppressedEmail { Id = Guid.NewGuid(), WorkspaceId = ws.Id, Email = $"stop-{Guid.NewGuid():N}@example.test", Reason = SuppressionReason.Manual, CreatedAt = now });
        await db.SaveChangesAsync();
        return ws;
    }

    /// <summary>Rows of <paramref name="type"/> visible through the context's filters.</summary>
    public static Task<int> CountVisibleAsync(ZenLeadDbContext db, Type type) => Invoke(nameof(CountVisible), type, db, null);

    /// <summary>Rows of <paramref name="type"/> returned by <c>ForWorkspace(workspaceId)</c> (tenant filter replaced, soft delete kept).</summary>
    public static Task<int> CountForWorkspaceAsync(ZenLeadDbContext db, Type type, Guid workspaceId) => Invoke(nameof(CountForWorkspace), type, db, workspaceId);

    /// <summary>Rows of <paramref name="type"/> really stored for the workspace, ignoring every filter except, for leads, soft delete.</summary>
    public static Task<int> CountStoredLiveAsync(ZenLeadDbContext db, Type type, Guid workspaceId) => Invoke(nameof(CountStoredLive), type, db, workspaceId);

    private static Task<int> Invoke(string method, Type type, ZenLeadDbContext db, Guid? workspaceId)
        => (Task<int>)typeof(TenantRowSeed).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, [db, workspaceId])!;

    private static Task<int> CountVisible<T>(ZenLeadDbContext db, Guid? _) where T : class, ITenantEntity => db.Set<T>().CountAsync();

    private static Task<int> CountForWorkspace<T>(ZenLeadDbContext db, Guid? workspaceId) where T : class, ITenantEntity
        => db.Set<T>().ForWorkspace(workspaceId!.Value).CountAsync();

    private static Task<int> CountStoredLive<T>(ZenLeadDbContext db, Guid? workspaceId) where T : class, ITenantEntity
    {
        var rows = db.Set<T>().IgnoreQueryFilters().Where(e => e.WorkspaceId == workspaceId!.Value);
        if (typeof(T) == typeof(Lead)) rows = rows.Where(e => ((Lead)(object)e).DeletedAt == null);
        return rows.CountAsync();
    }
}
