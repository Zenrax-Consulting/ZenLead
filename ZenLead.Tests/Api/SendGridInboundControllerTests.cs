using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZenLead.Api.Controllers.V1.Webhooks;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Api;

/// <summary>F25: the SendGrid Inbound Parse webhook end to end (anonymous; the query-string secret is the credential).</summary>
public class SendGridInboundControllerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Url = "/api/v1/webhooks/sendgrid/inbound";

    private sealed record Seeded(Guid WorkspaceId, Guid LeadId, string LeadEmail, Guid EnrollmentId);

    private async Task<Seeded> SeedAsync()
    {
        var (workspaceId, _, _) = await factory.CreateWorkspaceAndUserAsync($"Inbound {Guid.NewGuid():N}", $"owner-{Guid.NewGuid():N}@zenlead.test");
        var now = DateTime.UtcNow;
        var email = $"jane.{Guid.NewGuid():N}@example.com";
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = email, Status = LeadStatus.Contacted, CreatedAt = now };
        var step = new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Order = 1, SubjectTemplate = "s", BodyTemplate = "b" };
        var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "C", Status = CampaignStatus.Active, CreatedAt = now, Steps = [step] };
        var enrollment = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaign.Id, LeadId = lead.Id, Status = EnrollmentStatus.Active,
            NextStepOrder = 2, NextSendAt = now.AddDays(2), EnrolledAt = now
        };
        await factory.WithDbAsync(db =>
        {
            db.AddRange(lead, campaign, enrollment);
            return db.SaveChangesAsync();
        });
        return new Seeded(workspaceId, lead.Id, email, enrollment.Id);
    }

    private static MultipartFormDataContent Multipart(Dictionary<string, string> fields, byte[]? attachment = null)
    {
        var content = new MultipartFormDataContent();
        foreach (var (key, value) in fields) content.Add(new StringContent(value, Encoding.UTF8), key);
        if (attachment is not null)
        {
            content.Add(new StringContent("1"), "attachments");
            content.Add(new ByteArrayContent(attachment), "attachment1", "deck.pdf");
        }
        return content;
    }

    private Task<HttpResponseMessage> PostAsync(HttpContent content, string? secret = ApiFactory.InboundSecret)
        => factory.ClientFor(null).PostAsync(secret is null ? Url : $"{Url}?secret={Uri.EscapeDataString(secret)}", content);

    private Task<List<InboxMessage>> MessagesFor(Guid workspaceId)
        => factory.WithDbAsync(db => db.InboxMessages.IgnoreQueryFilters().Where(m => m.WorkspaceId == workspaceId).ToListAsync());

    private Task<List<InboundQuarantine>> QuarantineWithSubject(string subject)
        => factory.WithDbAsync(db => db.InboundQuarantine.Where(q => q.Subject == subject).ToListAsync());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData(ApiFactory.InboundSecret + "x")]
    public async Task Missing_or_wrong_secret_is_401_and_nothing_is_processed(string? secret)
    {
        var s = await SeedAsync();
        var fields = InboundFixtures.Load("gmail-reply", s.EnrollmentId, s.LeadEmail);

        var response = await PostAsync(Multipart(fields), secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await MessagesFor(s.WorkspaceId));
        Assert.Empty(await QuarantineWithSubject(fields["subject"]));
        Assert.Equal(EnrollmentStatus.Active, await factory.WithDbAsync(db =>
            db.CampaignEnrollments.IgnoreQueryFilters().Where(e => e.Id == s.EnrollmentId).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task The_secret_is_checked_before_the_body_is_read()
    {
        var oversized = Multipart(new Dictionary<string, string> { ["from"] = "a@b.test" }, attachment: new byte[SendGridInboundController.MaxRequestBytes + 1]);

        var response = await PostAsync(oversized, "wrong-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);                      // not 400/413: the form was never parsed
    }

    [Fact]
    public async Task Correct_secret_and_a_fixture_post_attach_the_reply()
    {
        var s = await SeedAsync();

        var response = await PostAsync(Multipart(InboundFixtures.Load("gmail-reply", s.EnrollmentId, s.LeadEmail)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = Assert.Single(await MessagesFor(s.WorkspaceId));
        Assert.Equal("Hi,\n\nYes, happy to chat next week. Tuesday works.\n\nJane", message.TextBody);
        Assert.DoesNotContain("<img", message.HtmlBody);                                    // the real sanitizer ran
        Assert.Equal(LeadStatus.Replied, await factory.WithDbAsync(db =>
            db.Leads.IgnoreQueryFilters().Where(l => l.Id == s.LeadId).Select(l => l.Status).SingleAsync()));
        Assert.Equal(EnrollmentStatus.Replied, await factory.WithDbAsync(db =>
            db.CampaignEnrollments.IgnoreQueryFilters().Where(e => e.Id == s.EnrollmentId).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task Redelivery_of_the_same_post_is_acknowledged_without_a_second_row()
    {
        var s = await SeedAsync();
        var fields = InboundFixtures.Load("outlook-reply", s.EnrollmentId, s.LeadEmail);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Multipart(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Multipart(fields))).StatusCode);

        Assert.Single(await MessagesFor(s.WorkspaceId));
    }

    [Fact]
    public async Task Out_of_office_fixture_is_stored_and_the_sequence_continues()
    {
        var s = await SeedAsync();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Multipart(InboundFixtures.Load("ooo-autoreply", s.EnrollmentId, s.LeadEmail)))).StatusCode);

        Assert.Equal(ReplyClassification.OutOfOffice, Assert.Single(await MessagesFor(s.WorkspaceId)).Classification);
        Assert.Equal(EnrollmentStatus.Active, await factory.WithDbAsync(db =>
            db.CampaignEnrollments.IgnoreQueryFilters().Where(e => e.Id == s.EnrollmentId).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_non_form_post_is_400()
    {
        var response = await PostAsync(JsonContent.Create(new { from = "a@b.test" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unparseable_post_is_acknowledged_and_quarantined()
    {
        var subject = $"no from {Guid.NewGuid():N}";

        var response = await PostAsync(Multipart(new Dictionary<string, string> { ["to"] = "x@reply.zenlead.test", ["subject"] = subject, ["text"] = "body" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var q = Assert.Single(await QuarantineWithSubject(subject));
        Assert.Equal(InboundQuarantine.ParseError, q.Reason);
    }

    [Fact]
    public async Task Unmatched_mail_is_acknowledged_and_quarantined()
    {
        var fields = InboundFixtures.Load("gmail-reply", Guid.NewGuid(), $"stranger.{Guid.NewGuid():N}@example.com");
        fields["subject"] = $"unmatched {Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Multipart(fields))).StatusCode);

        Assert.Equal(InboundQuarantine.NoMatch, Assert.Single(await QuarantineWithSubject(fields["subject"])).Reason);
    }

    [Fact]
    public async Task An_attachment_is_accepted_and_ignored()
    {
        var s = await SeedAsync();
        var fields = InboundFixtures.Load("apple-mail-reply", s.EnrollmentId, s.LeadEmail);

        var response = await PostAsync(Multipart(fields, attachment: new byte[3 * 1024 * 1024]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await MessagesFor(s.WorkspaceId));
    }

    [Fact]
    public async Task A_post_over_the_form_limits_is_acknowledged_and_quarantined_as_too_large()
    {
        var before = await factory.WithDbAsync(db => db.InboundQuarantine.CountAsync(q => q.Reason == InboundQuarantine.TooLarge));
        var fields = new Dictionary<string, string> { ["from"] = "a@b.test", ["to"] = "x@reply.zenlead.test", ["text"] = "hi" };

        var response = await PostAsync(Multipart(fields, attachment: new byte[SendGridInboundController.MaxRequestBytes + 1]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 1, await factory.WithDbAsync(db => db.InboundQuarantine.CountAsync(q => q.Reason == InboundQuarantine.TooLarge)));
    }
}
