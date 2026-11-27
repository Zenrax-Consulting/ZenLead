using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ZenLead.Api.Controllers.V1.Webhooks;
using ZenLead.Application.Dtos.Analytics;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api;

/// <summary>The API with a generated Event Webhook verification key (never a real one), set last so user-secrets can't override it.</summary>
public class EventWebhookApiFactory : ApiFactory
{
    public EventWebhookSigner Signer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SendGrid:EventWebhook:PublicKey"] = Signer.PublicKey,
            ["SendGrid:EventWebhook:MaxAgeHours"] = "26"
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Signer.Dispose();
    }

    /// <summary>A workspace with one campaign email sent yesterday; returns its ids.</summary>
    public async Task<(Guid WorkspaceId, Guid CampaignId, Guid MessageId, Guid LeadId, string AccessToken)> SeedSentMessageAsync(DateTime? sentAt = null)
    {
        var (workspaceId, _, token) = await CreateWorkspaceAndUserAsync($"Events {Guid.NewGuid():N}", $"owner-{Guid.NewGuid():N}@zenlead.test");
        var at = sentAt ?? DateTime.UtcNow.AddDays(-1);
        var ids = await WithDbAsync(async db =>
        {
            var step = new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Order = 1, SubjectTemplate = "Hello", BodyTemplate = "b" };
            var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Q4", Status = CampaignStatus.Active, CreatedAt = at, Steps = [step] };
            var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = $"jane.{Guid.NewGuid():N}@example.com", Status = LeadStatus.Contacted, CreatedAt = at };
            var enrollment = new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaign.Id, LeadId = lead.Id, EnrolledAt = at, NextStepOrder = 2 };
            var message = new EmailMessage
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaign.Id, EnrollmentId = enrollment.Id, StepId = step.Id, LeadId = lead.Id,
                Subject = "Hello", Body = "b", Status = EmailMessageStatus.Sent, QueuedAt = at, SentAt = at
            };
            db.AddRange(campaign, lead, enrollment, message);
            await db.SaveChangesAsync();
            return (campaign.Id, message.Id, lead.Id);
        });
        return (workspaceId, ids.Item1, ids.Item2, ids.Item3, token);
    }
}

public class SendGridEventsControllerTests(EventWebhookApiFactory factory) : IClassFixture<EventWebhookApiFactory>
{
    private const string Url = "/api/v1/webhooks/sendgrid/events";

    private static HttpRequestMessage Post(byte[] body, string? timestamp, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (timestamp is not null) request.Headers.TryAddWithoutValidation(SendGridEventsController.TimestampHeader, timestamp);
        if (signature is not null) request.Headers.TryAddWithoutValidation(SendGridEventsController.SignatureHeader, signature);
        return request;
    }

    private HttpRequestMessage Signed(byte[] body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return Post(body, ts, factory.Signer.Sign(ts, body));
    }

    private Task<EmailMessage> Message(Guid id) => factory.WithDbAsync(db => db.EmailMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id));
    private Task<int> Processed(string id) => factory.WithDbAsync(db => db.ProcessedWebhookEvents.CountAsync(p => p.SgEventId == id));

    [Fact]
    public async Task Valid_signature_applies_the_events()
    {
        var s = await factory.SeedSentMessageAsync();
        var body = EventFixtures.Body(
            EventFixtures.Event("delivered", s.MessageId, s.WorkspaceId, DateTime.UtcNow.AddMinutes(-10)),
            EventFixtures.Event("open", s.MessageId, s.WorkspaceId, DateTime.UtcNow.AddMinutes(-5)));

        var response = await factory.ClientFor(null).SendAsync(Signed(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var m = await Message(s.MessageId);
        Assert.Equal(EmailMessageStatus.Delivered, m.Status);
        Assert.NotNull(m.OpenedAt);
    }

    [Fact]
    public async Task Bad_signature_is_401_and_nothing_is_processed()
    {
        var s = await factory.SeedSentMessageAsync();
        var eventId = $"evt-{Guid.NewGuid():N}";
        var body = EventFixtures.Body(EventFixtures.Event("bounce", s.MessageId, s.WorkspaceId, DateTime.UtcNow, eventId, status: "5.1.1"));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var stranger = new EventWebhookSigner();

        var client = factory.ClientFor(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(body, ts, stranger.Sign(ts, body)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(body, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(body, ts, "garbage"))).StatusCode);

        Assert.Equal(0, await Processed(eventId));                             // the use case never ran
        Assert.Equal(EmailMessageStatus.Sent, (await Message(s.MessageId)).Status);
    }

    [Fact]
    public async Task Replaying_the_captured_request_is_200_and_changes_nothing_but_one_changed_character_is_401()
    {
        var s = await factory.SeedSentMessageAsync();
        var body = EventFixtures.Body(EventFixtures.Event("open", s.MessageId, s.WorkspaceId, DateTime.UtcNow.AddMinutes(-5)));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = factory.Signer.Sign(ts, body);
        var client = factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(body, ts, sig))).StatusCode);
        var first = (await Message(s.MessageId)).OpenedAt;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(body, ts, sig))).StatusCode);
        Assert.Equal(first, (await Message(s.MessageId)).OpenedAt);

        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"open\"", "\"opfn\""));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(tampered, ts, sig))).StatusCode);
    }

    [Fact]
    public async Task Signed_body_that_is_not_an_event_array_is_400()
    {
        var response = await factory.ClientFor(null).SendAsync(Signed(Encoding.UTF8.GetBytes("""{"not":"an array"}""")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Events_for_unknown_messages_are_acknowledged()
    {
        var body = EventFixtures.Body(EventFixtures.Event("delivered", Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(null).SendAsync(Signed(body))).StatusCode);
    }
}

public class AnalyticsControllerTests(EventWebhookApiFactory factory) : IClassFixture<EventWebhookApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [Fact]
    public async Task Workspace_summary_reports_the_cohort_and_flags_opens_as_approximate()
    {
        var s = await factory.SeedSentMessageAsync();
        await factory.WithDbAsync(async db =>
        {
            var m = await db.EmailMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == s.MessageId);
            m.DeliveredAt = m.SentAt;
            m.OpenedAt = m.SentAt!.Value.AddMinutes(5);
            return await db.SaveChangesAsync();
        });

        var response = await factory.ClientFor(s.AccessToken).GetAsync("/api/v1/analytics/workspace/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"opensApproximate\":true", raw);
        var w = JsonSerializer.Deserialize<WorkspaceAnalytics>(raw, Json)!;
        Assert.Equal((1, 1, 1), (w.Totals.Sent, w.Totals.Delivered, w.Totals.Opened));
        Assert.Equal(30, w.Trend.Count);
        Assert.Single(w.Campaigns);
    }

    [Fact]
    public async Task Campaign_summary_and_another_workspaces_campaign_is_404()
    {
        var a = await factory.SeedSentMessageAsync();
        var b = await factory.SeedSentMessageAsync();

        var own = await factory.ClientFor(a.AccessToken).GetFromJsonAsync<CampaignAnalytics>($"/api/v1/analytics/campaigns/{a.CampaignId}/summary", Json);
        Assert.Equal(1, own!.Totals.Sent);
        Assert.Single(own.Steps);

        var foreign = await factory.ClientFor(b.AccessToken).GetAsync($"/api/v1/analytics/campaigns/{a.CampaignId}/summary");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var bSummary = await factory.ClientFor(b.AccessToken).GetFromJsonAsync<WorkspaceAnalytics>("/api/v1/analytics/workspace/summary", Json);
        Assert.Equal(1, bSummary!.Totals.Sent);                                // only B's own message
        Assert.Equal(b.CampaignId, Assert.Single(bSummary.Campaigns).CampaignId);
    }

    [Theory]
    [InlineData("?from=2026-03-10&to=2026-03-01")]
    [InlineData("?from=2024-01-01&to=2026-03-01")]
    [InlineData("?from=yesterday")]
    public async Task Invalid_range_is_400(string query)
    {
        var s = await factory.SeedSentMessageAsync();
        var response = await factory.ClientFor(s.AccessToken).GetAsync("/api/v1/analytics/workspace/summary" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Anonymous_is_401()
        => Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientFor(null).GetAsync("/api/v1/analytics/workspace/summary")).StatusCode);
}
