using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>
/// The unique indexes the app relies on as its last line of defence (F11, F22, F23, F25, F28), checked on SQL Server with its real
/// collation. SQLite is case-sensitive by default, so the lead email check in particular can only be proven here.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class UniqueIndexTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => sql.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LeadEmail_IsUniquePerWorkspace_IgnoringCase()
    {
        await using var db = sql.CreateContext();
        var ws = await new WorkspaceBuilder().SaveAsync(db);
        await new LeadBuilder(ws.Id).WithEmail("ann@example.test").SaveAsync(db);

        var mixedCase = new LeadBuilder(ws.Id).Build();
        mixedCase.Email = "Ann@Example.TEST";          // bypass the builder's lower-casing: the index alone must catch it
        db.Leads.Add(mixedCase);

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_Leads_WorkspaceId_Email");
    }

    [Fact]
    public async Task LeadEmail_MayRepeatAcrossWorkspaces()
    {
        await using var db = sql.CreateContext();
        var a = await new WorkspaceBuilder().SaveAsync(db);
        var b = await new WorkspaceBuilder().SaveAsync(db);

        await new LeadBuilder(a.Id).WithEmail("shared@example.test").SaveAsync(db);
        await new LeadBuilder(b.Id).WithEmail("shared@example.test").SaveAsync(db);

        Assert.Equal(2, await db.Leads.IgnoreQueryFilters().CountAsync(l => l.Email == "shared@example.test"));
    }

    [Fact]
    public async Task SoftDeletedLead_StillBlocksTheSameEmail()
    {
        // documented F11 behaviour (parking lot: no restore action yet): soft delete keeps the row, so the address stays taken
        await using var db = sql.CreateContext();
        var ws = await new WorkspaceBuilder().SaveAsync(db);
        await new LeadBuilder(ws.Id).WithEmail("gone@example.test").Deleted(DateTime.UtcNow).SaveAsync(db);

        db.Leads.Add(new LeadBuilder(ws.Id).WithEmail("gone@example.test").Build());

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_Leads_WorkspaceId_Email");
    }

    [Fact]
    public async Task Enrollment_IsUniquePerCampaignAndLead()
    {
        await using var db = sql.CreateContext();
        var (_, campaign, lead) = await CampaignWithLeadAsync(db);
        await new EnrollmentBuilder(campaign, lead).SaveAsync(db);

        db.CampaignEnrollments.Add(new EnrollmentBuilder(campaign, lead).Build());

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_CampaignEnrollments_CampaignId_LeadId");
    }

    [Fact]
    public async Task EmailMessage_IsUniquePerEnrollmentAndStep_TheDoubleSendGuard()
    {
        await using var db = sql.CreateContext();
        var (_, campaign, lead) = await CampaignWithLeadAsync(db);
        var enrollment = await new EnrollmentBuilder(campaign, lead).SaveAsync(db);
        await new EmailMessageBuilder(enrollment, campaign.Steps[0]).WithStatus(EmailMessageStatus.Queued).SaveAsync(db);

        db.EmailMessages.Add(new EmailMessageBuilder(enrollment, campaign.Steps[0]).WithStatus(EmailMessageStatus.Queued).Build());

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_EmailMessages_EnrollmentId_StepId");
    }

    [Fact]
    public async Task ProcessedWebhookEvent_IsUniqueOnSgEventId()
    {
        await using (var first = sql.CreateContext())
        {
            first.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { SgEventId = "sg-event-1", ReceivedAt = DateTime.UtcNow });
            await first.SaveChangesAsync();
        }

        await using var second = sql.CreateContext();
        second.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { SgEventId = "sg-event-1", ReceivedAt = DateTime.UtcNow });

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync()), "PK_ProcessedWebhookEvents");
    }

    [Fact]
    public async Task InboundMessageId_IsUniquePerWorkspace_ButMayRepeatAcrossWorkspaces()
    {
        await using var db = sql.CreateContext();
        var a = await new WorkspaceBuilder().SaveAsync(db);
        var b = await new WorkspaceBuilder().SaveAsync(db);
        var leadA = await new LeadBuilder(a.Id).SaveAsync(db);
        var leadB = await new LeadBuilder(b.Id).SaveAsync(db);
        await new InboxThreadBuilder(leadA).WithReply("Yes please", messageId: "<dup@mail.example.test>").SaveAsync(db);
        await new InboxThreadBuilder(leadB).WithReply("Yes please", messageId: "<dup@mail.example.test>").SaveAsync(db);   // other tenant: fine

        var again = new InboxThreadBuilder(leadA).WithReply("Redelivered", messageId: "<dup@mail.example.test>");
        db.InboxThreads.Add(again.Build());
        db.InboxMessages.AddRange(again.Messages);

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_InboxMessages_WorkspaceId_RawMessageId");
    }

    [Fact]
    public async Task SuppressedEmail_IsUniquePerWorkspace_IgnoringCase()
    {
        await using var db = sql.CreateContext();
        var ws = await new WorkspaceBuilder().SaveAsync(db);
        db.SuppressedEmails.Add(new SuppressedEmail { Id = Guid.NewGuid(), WorkspaceId = ws.Id, Email = "stop@example.test", Reason = SuppressionReason.Unsubscribed, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.SuppressedEmails.Add(new SuppressedEmail { Id = Guid.NewGuid(), WorkspaceId = ws.Id, Email = "STOP@example.test", Reason = SuppressionReason.Unsubscribed, CreatedAt = DateTime.UtcNow });

        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()), "IX_SuppressedEmails_WorkspaceId_Email");
    }

    private static async Task<(Workspace, Campaign, Lead)> CampaignWithLeadAsync(ZenLead.Infrastructure.Persistence.ZenLeadDbContext db)
    {
        var ws = await new WorkspaceBuilder().SaveAsync(db);
        var campaign = await new CampaignBuilder(ws.Id).SaveAsync(db);
        var lead = await new LeadBuilder(ws.Id).SaveAsync(db);
        return (ws, campaign, lead);
    }

    /// <summary>SQL Server error 2601 (unique index) or 2627 (unique constraint / primary key), on the expected index.</summary>
    private static void AssertUniqueViolation(DbUpdateException ex, string indexName)
    {
        var sqlEx = Assert.IsType<SqlException>(ex.InnerException);
        Assert.Contains(sqlEx.Number, new[] { 2601, 2627 });
        Assert.Contains(indexName, sqlEx.Message);
    }
}
