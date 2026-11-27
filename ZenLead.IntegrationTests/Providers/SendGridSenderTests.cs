using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Providers;

/// <summary>
/// The campaign sender (F23) through the real SendGrid SDK (F18) against a WireMock SendGrid, on SQL Server. Covers the outbound
/// payload and how each provider answer is recorded.
/// <para>
/// Known gap, tested as built: F18 treats 429, 5xx and client timeouts as retryable, so the message row is deleted and the send is
/// tried again after a backoff. A 5xx or timeout <i>after</i> SendGrid accepted the request can therefore email the lead twice;
/// the T03 spec wanted those treated as "uncertain" (never resent). Logged in docs/parking-lot.md for F29.
/// </para>
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class SendGridSenderTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    private DueSend _due = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        await using var db = factory.Sql.CreateContext();
        _due = await SendingSeed.DueEnrollmentAsync(db, Now);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DateTime Now => factory.Clock.GetUtcNow().UtcDateTime;

    private Task<SendRunSummary> RunSenderAsync()
        => factory.InScopeAsync(sp => sp.GetRequiredService<SendDueEmailsUseCase>().ExecuteAsync(CancellationToken.None));

    [Fact]
    public async Task Payload_CarriesSenderReplyToTrackingArgsUnsubscribeHeadersAndBothParts()
    {
        var summary = await RunSenderAsync();

        Assert.Equal(1, summary.Sent);
        var request = Assert.Single(factory.Stubs.SendGrid.MailSends);
        Assert.Equal("Bearer SG.integration-test-not-a-key", request.RequestMessage!.Headers!["Authorization"].Single());
        var json = StubServer.BodyJson(request);
        var message = await SingleMessageAsync();

        Assert.Equal("business@zenraxconsulting.com", json.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal(ReplyAddress.Encode(_due.Enrollment.Id, "reply.zenlead.test"), json.GetProperty("reply_to").GetProperty("email").GetString());
        var to = json.GetProperty("personalizations")[0].GetProperty("to")[0];
        Assert.Equal(_due.Lead.Email, to.GetProperty("email").GetString());
        Assert.Equal("Hello Ada", json.GetProperty("subject").GetString());

        var args = Section(json, "custom_args");
        Assert.Equal(message.Id.ToString(), args.GetProperty("emailMessageId").GetString());
        Assert.Equal(_due.Workspace.Id.ToString(), args.GetProperty("workspaceId").GetString());
        Assert.Equal(_due.Campaign.Id.ToString(), args.GetProperty("campaignId").GetString());

        var headers = Section(json, "headers");
        Assert.StartsWith("<https://app.zenlead.test/api/v1/unsubscribe/", headers.GetProperty("List-Unsubscribe").GetString());
        Assert.Equal("List-Unsubscribe=One-Click", headers.GetProperty("List-Unsubscribe-Post").GetString());

        var content = json.GetProperty("content").EnumerateArray().ToDictionary(c => c.GetProperty("type").GetString()!, c => c.GetProperty("value").GetString()!);
        Assert.Contains("Quick question about Acme.", content["text/plain"]);
        Assert.Contains("https://app.zenlead.test/unsubscribe/", content["text/plain"]);      // footer appended after any AI
        Assert.Contains("<p>", content["text/html"]);

        var settings = json.GetProperty("tracking_settings");
        Assert.False(settings.GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.True(settings.GetProperty("open_tracking").GetProperty("enable").GetBoolean());

        Assert.Equal(EmailMessageStatus.Sent, message.Status);
        Assert.Equal(SendGridStub.MessageId, message.ProviderMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientAnswer_LeavesNoMessage_AndRetriesAfterTheFirstBackoff(HttpStatusCode status)
    {
        factory.Stubs.SendGrid.RespondWith(status);

        var summary = await RunSenderAsync();

        Assert.Equal(1, summary.Retrying);
        Assert.Single(factory.Stubs.SendGrid.MailSends);
        await using var db = factory.Sql.CreateContext();
        Assert.Empty(await db.EmailMessages.IgnoreQueryFilters().Where(m => m.EnrollmentId == _due.Enrollment.Id).ToListAsync());
        var enrollment = await EnrollmentAsync(db);
        Assert.Equal(EnrollmentStatus.Active, enrollment.Status);
        Assert.Equal(1, enrollment.SendAttempts);
        Assert.Equal(Now.AddMinutes(5), enrollment.NextSendAt!.Value, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PermanentRejection_FailsTheMessageAndTheEnrollment_WithTheProviderError()
    {
        factory.Stubs.SendGrid.RespondWith(HttpStatusCode.BadRequest, "The to email does not contain a valid address.");

        var summary = await RunSenderAsync();

        Assert.Equal(1, summary.Failed);
        var message = await SingleMessageAsync();
        Assert.Equal(EmailMessageStatus.Failed, message.Status);
        Assert.StartsWith("SendGrid 400:", message.Error);
        Assert.Contains("does not contain a valid address", message.Error);
        await using var db = factory.Sql.CreateContext();
        var enrollment = await EnrollmentAsync(db);
        Assert.Equal(EnrollmentStatus.Failed, enrollment.Status);
        Assert.Null(enrollment.NextSendAt);
    }

    [Fact]
    public async Task ClientTimeoutAfterSendGridAccepted_IsRetried_SoTheLeadCanGetTheEmailTwice_KnownGap()
    {
        factory.Stubs.SendGrid.RespondSlowly();

        var first = await RunSenderAsync();
        var requestsSoFar = (await factory.Stubs.SendGrid.WaitForRequestsAsync(SendGridStub.MailSendPath, 1)).Count;
        factory.Stubs.Reset();                                   // SendGrid answers normally again
        factory.Clock.Advance(TimeSpan.FromMinutes(6));          // past the first 5-minute backoff
        var second = await RunSenderAsync();

        Assert.Equal(1, first.Retrying);
        Assert.Equal(1, requestsSoFar);                          // SendGrid did receive (and, in reality, may have delivered) the first one
        Assert.Equal(1, second.Sent);
        Assert.Single(factory.Stubs.SendGrid.MailSends);         // ...and the same step went out again: two requests in total
        Assert.Equal(EmailMessageStatus.Sent, (await SingleMessageAsync()).Status);
    }

    [Fact]
    public async Task AStaleQueuedMessage_IsMarkedUncertain_AndNeverSentAgain()
    {
        // the process died between inserting the Queued row and recording SendGrid's answer
        await using (var db = factory.Sql.CreateContext())
            await new EmailMessageBuilder(_due.Enrollment, _due.Campaign.Steps[0]).WithStatus(EmailMessageStatus.Queued)
                .SentAt(Now.AddMinutes(-11)).WithProviderMessageId(null).SaveAsync(db);
        await using (var db = factory.Sql.CreateContext())
        {
            var queued = await db.EmailMessages.IgnoreQueryFilters().SingleAsync(m => m.EnrollmentId == _due.Enrollment.Id);
            queued.SentAt = null;
            await db.SaveChangesAsync();
        }

        var summary = await RunSenderAsync();

        Assert.Equal(1, summary.Failed);
        Assert.Empty(factory.Stubs.SendGrid.MailSends);
        var message = await SingleMessageAsync();
        Assert.Equal(EmailMessageStatus.Failed, message.Status);
        Assert.Equal(SendDueEmailsUseCase.UncertainError, message.Error);
        await using var check = factory.Sql.CreateContext();
        Assert.Equal(2, (await EnrollmentAsync(check)).NextStepOrder);   // the step is skipped for this lead, not retried
    }

    [Fact]
    public async Task ARecipientOutsideTheAllowList_IsBlocked_WithoutCallingSendGrid()
    {
        await using (var db = factory.Sql.CreateContext())
        {
            var lead = await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == _due.Lead.Id);
            lead.Email = "real.prospect@not-allowed.example.com";
            await db.SaveChangesAsync();
        }

        var summary = await RunSenderAsync();

        Assert.Equal(1, summary.Blocked);
        Assert.Empty(factory.Stubs.SendGrid.MailSends);
    }

    private static JsonElement Section(JsonElement json, string name)
        => json.TryGetProperty(name, out var top) ? top : json.GetProperty("personalizations")[0].GetProperty(name);

    private async Task<EmailMessage> SingleMessageAsync()
    {
        await using var db = factory.Sql.CreateContext();
        return await db.EmailMessages.IgnoreQueryFilters().SingleAsync(m => m.EnrollmentId == _due.Enrollment.Id);
    }

    private Task<CampaignEnrollment> EnrollmentAsync(ZenLead.Infrastructure.Persistence.ZenLeadDbContext db)
        => db.CampaignEnrollments.IgnoreQueryFilters().SingleAsync(e => e.Id == _due.Enrollment.Id);
}
