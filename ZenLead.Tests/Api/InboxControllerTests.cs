using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api;

public class InboxApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:ReplyPermitLimit", "10000");
    }
}

public class InboxRateLimitedApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:ReplyPermitLimit", "2");
    }
}

public class InboxReplyRateLimitTests(InboxRateLimitedApiFactory factory) : IClassFixture<InboxRateLimitedApiFactory>
{
    [Fact]
    public async Task Replies_are_rate_limited_per_workspace()
    {
        var (_, _, token) = await factory.CreateWorkspaceAndUserAsync($"RL {Guid.NewGuid():N}", $"rl-{Guid.NewGuid():N}@zenlead.test");
        var client = factory.ClientFor(token);
        var url = $"/api/v1/inbox/threads/{Guid.NewGuid()}/reply";
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(url, new ReplyRequest(Guid.NewGuid(), "hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(url, new ReplyRequest(Guid.NewGuid(), "hi"))).StatusCode);
    }
}

/// <summary>F27: the inbox API end to end — tenant isolation first, then read state, replies and manual reclassification.</summary>
public class InboxControllerTests(InboxApiFactory factory) : IClassFixture<InboxApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Tenant(Guid WorkspaceId, HttpClient Client);

    private async Task<Tenant> NewTenantAsync()
    {
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync($"Inbox {Guid.NewGuid():N}", $"owner-{Guid.NewGuid():N}@zenlead.test");
        return new Tenant(workspaceId, factory.ClientFor(token));
    }

    private Task<InboxSeed.Conversation> SeedAsync(Guid workspaceId, LeadStatus status = LeadStatus.Replied, bool needsReview = false,
        ReplyClassification classification = ReplyClassification.Unclassified, bool autoReply = false, string? reply = "Sounds good")
        => factory.WithDbAsync(db => InboxSeed.ConversationAsync(db, workspaceId, DateTime.UtcNow.AddDays(-1), status: status,
            needsReview: needsReview, classification: classification, autoReply: autoReply, replyText: reply));

    private static async Task<T> Read<T>(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<T>(Json))!;

    private static string Url(Guid threadId, string suffix = "") => $"/api/v1/inbox/threads/{threadId}{suffix}";

    // ---- tenant isolation (priority) ----

    [Fact]
    public async Task Another_workspace_gets_404_on_every_thread_route_and_never_sees_the_thread()
    {
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var thread = (await SeedAsync(a.WorkspaceId)).Thread;
        await SeedAsync(b.WorkspaceId);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(Url(thread.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync(Url(thread.Id, "/read"), new SetReadRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync(Url(thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), "hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.Client.PostAsJsonAsync(Url(thread.Id, "/classification"), new ReclassifyRequest(ReplyClassification.Interested), Json)).StatusCode);

        var list = await Read<PagedResult<ThreadListItem>>(await b.Client.GetAsync("/api/v1/inbox/threads"));
        Assert.DoesNotContain(list.Items, i => i.Id == thread.Id);
        Assert.Single(list.Items);
        Assert.Equal(1, (await Read<UnreadCountResponse>(await a.Client.GetAsync("/api/v1/inbox/unread-count"))).Count);
        Assert.Equal(1, (await Read<UnreadCountResponse>(await b.Client.GetAsync("/api/v1/inbox/unread-count"))).Count);
        // nothing changed in A: still unread, nothing sent to A's lead
        Assert.False(await factory.WithDbAsync(db => db.InboxThreads.IgnoreQueryFilters().Where(t => t.Id == thread.Id).Select(t => t.IsRead).SingleAsync()));
    }

    [Fact]
    public async Task Super_admin_gets_403_on_the_inbox()
    {
        var (_, token) = await factory.CreateSuperAdminAsync();
        var client = factory.ClientFor(token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/inbox/threads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/inbox/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Url(Guid.NewGuid(), "/reply"), new ReplyRequest(Guid.NewGuid(), "hi"))).StatusCode);
    }

    // ---- list & read semantics ----

    [Fact]
    public async Task List_filters_and_validates()
    {
        var t = await NewTenantAsync();
        var interested = await SeedAsync(t.WorkspaceId, classification: ReplyClassification.Interested);
        await SeedAsync(t.WorkspaceId, needsReview: true, classification: ReplyClassification.Unsubscribe);

        var page = await Read<PagedResult<ThreadListItem>>(await t.Client.GetAsync("/api/v1/inbox/threads?classification=Interested&page=1&pageSize=10"));
        Assert.Equal(interested.Thread.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, (await Read<PagedResult<ThreadListItem>>(await t.Client.GetAsync("/api/v1/inbox/threads?needsReview=true"))).Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Client.GetAsync("/api/v1/inbox/threads?pageSize=500")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Client.GetAsync($"/api/v1/inbox/threads?q={new string('x', 101)}")).StatusCode);
    }

    [Fact]
    public async Task Opening_a_thread_marks_it_read_once_a_new_reply_makes_it_unread_and_mark_unread_is_explicit()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        async Task<int> Unread() => (await Read<UnreadCountResponse>(await t.Client.GetAsync("/api/v1/inbox/unread-count"))).Count;
        Assert.Equal(1, await Unread());

        var detail = await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)));
        Assert.Equal(2, detail.Messages.Count);
        Assert.True(detail.CanReply);
        Assert.Equal("Q4 outreach", detail.CampaignName);
        Assert.Equal(0, await Unread());
        Assert.Equal(HttpStatusCode.OK, (await t.Client.GetAsync(Url(c.Thread.Id))).StatusCode);   // idempotent
        Assert.Equal(0, await Unread());

        // a new inbound reply arrives through the F25 store: unread again
        await factory.WithDbAsync(async db =>
        {
            var store = new InboundStore(db, TimeProvider.System);
            return await store.TryAddInboundAsync(InboxSeed.Inbound(t.WorkspaceId, c.Thread.Id, DateTime.UtcNow, "Re: Quick question", "One more thing",
                c.Lead.Email, $"later-{Guid.NewGuid():N}@mail.test"), default);
        });
        Assert.Equal(1, await Unread());

        await t.Client.GetAsync(Url(c.Thread.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/read"), new SetReadRequest(false))).StatusCode);
        Assert.Equal(1, await Unread());
        Assert.Equal(HttpStatusCode.NoContent, (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/read"), new SetReadRequest(true))).StatusCode);
        Assert.Equal(0, await Unread());
    }

    [Fact]
    public async Task A_suppressed_lead_cannot_be_replied_to_and_the_detail_says_why()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId, status: LeadStatus.Unsubscribed);
        var sentBefore = factory.Email.Sent.Count;

        var detail = await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)));
        Assert.False(detail.CanReply);
        Assert.NotNull(detail.CannotReplyReason);

        var response = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), "hi"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("suppressed", (await Read<JsonElement>(response)).GetProperty("code").GetString());
        Assert.Equal(sentBefore, factory.Email.Sent.Count);
        Assert.Equal(2, (await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)))).Messages.Count);   // no row
    }

    // ---- replies ----

    [Fact]
    public async Task Reply_returns_201_then_200_for_the_same_request_id_and_sends_once()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        var request = new ReplyRequest(Guid.NewGuid(), "Tuesday at 10?");
        var sentBefore = factory.Email.Sent.Count(m => m.To.Address == c.Lead.Email);

        var first = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), request);
        var second = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var message = await Read<MessageDto>(first);
        Assert.Equal(OutboundDeliveryStatus.Sent, message.DeliveryStatus);
        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Equal(message.Id, (await Read<MessageDto>(second)).Id);
        Assert.Equal(sentBefore + 1, factory.Email.Sent.Count(m => m.To.Address == c.Lead.Email));
    }

    [Fact]
    public async Task A_failed_send_is_502_and_the_failed_row_shows_in_the_thread()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        factory.Email.Result = SendResult.Fail("SendGrid said no", retryable: true);
        HttpResponseMessage response;
        try { response = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), "hello")); }
        finally { factory.Email.Result = SendResult.Ok("fake-id"); }

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await Read<JsonElement>(response);
        Assert.Equal("send_failed", body.GetProperty("code").GetString());
        Assert.Equal("SendGrid said no", body.GetProperty("message").GetString());
        var last = (await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)))).Messages[^1];
        Assert.Equal(OutboundDeliveryStatus.Failed, last.DeliveryStatus);
        Assert.Equal("SendGrid said no", last.DeliveryError);

        Assert.Equal(HttpStatusCode.Created, (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), "hello"))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\0nul")]
    public async Task Reply_validation_rejects_bad_bodies(string body)
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), body))).StatusCode);
    }

    [Fact]
    public async Task Reply_validation_rejects_long_bodies_and_empty_request_ids()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), new string('x', 10_001)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.Empty, "hi"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/reply"), new ReplyRequest(Guid.NewGuid(), new string('x', 10_000)))).StatusCode);
    }

    // ---- manual reclassification (through F26's ClassificationEffects) ----

    [Fact]
    public async Task Reclassify_as_unsubscribe_suppresses_the_lead_and_stops_other_enrollments()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        var otherEnrollment = await factory.WithDbAsync(async db =>
        {
            var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = t.WorkspaceId, Name = "Other", Status = CampaignStatus.Active, CreatedAt = DateTime.UtcNow };
            var e = new CampaignEnrollment
            {
                Id = Guid.NewGuid(), WorkspaceId = t.WorkspaceId, CampaignId = campaign.Id, LeadId = c.Lead.Id, Status = EnrollmentStatus.Active,
                NextSendAt = DateTime.UtcNow.AddDays(1), EnrolledAt = DateTime.UtcNow
            };
            db.Campaigns.Add(campaign);
            db.CampaignEnrollments.Add(e);
            await db.SaveChangesAsync();
            return e.Id;
        });

        var response = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/classification"), new ReclassifyRequest(ReplyClassification.Unsubscribe), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReplyClassification.Unsubscribe, (await Read<ThreadListItem>(response)).Classification);
        await factory.WithDbAsync(async db =>
        {
            Assert.Equal(LeadStatus.Unsubscribed, (await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == c.Lead.Id)).Status);
            Assert.True(await db.SuppressedEmails.IgnoreQueryFilters().AnyAsync(s => s.WorkspaceId == t.WorkspaceId && s.Email == c.Lead.Email));
            Assert.Equal(EnrollmentStatus.Unsubscribed, (await db.CampaignEnrollments.IgnoreQueryFilters().SingleAsync(e => e.Id == otherEnrollment)).Status);
            var reply = await db.InboxMessages.IgnoreQueryFilters().SingleAsync(m => m.ThreadId == c.Thread.Id && m.Direction == MessageDirection.Inbound);
            Assert.Equal(ClassificationSource.Manual, reply.ClassifiedBy);
            Assert.Equal(1m, reply.ClassificationConfidence);
            return 0;
        });
        Assert.False((await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)))).CanReply);
    }

    [Fact]
    public async Task Reclassify_as_interested_clears_needs_review()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId, needsReview: true, classification: ReplyClassification.Unsubscribe);

        var item = await Read<ThreadListItem>(await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/classification"),
            new ReclassifyRequest(ReplyClassification.Interested), Json));

        Assert.Equal(ReplyClassification.Interested, item.Classification);
        Assert.False(item.NeedsReview);
        Assert.Equal(LeadStatus.Replied, (await Read<ThreadDetail>(await t.Client.GetAsync(Url(c.Thread.Id)))).LeadStatus);
    }

    [Theory]
    [InlineData("Unclassified")]
    [InlineData("Bounce")]
    [InlineData("Maybe")]
    public async Task Reclassify_rejects_labels_a_human_cannot_set(string label)
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId);
        var response = await t.Client.PostAsync(Url(c.Thread.Id, "/classification"),
            new StringContent($$"""{"classification":"{{label}}"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reclassify_without_a_human_reply_is_a_clear_409()
    {
        var t = await NewTenantAsync();
        var c = await SeedAsync(t.WorkspaceId, autoReply: true, classification: ReplyClassification.OutOfOffice);

        var response = await t.Client.PostAsJsonAsync(Url(c.Thread.Id, "/classification"), new ReclassifyRequest(ReplyClassification.Interested), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("nothing_to_classify", (await Read<JsonElement>(response)).GetProperty("code").GetString());
    }
}
