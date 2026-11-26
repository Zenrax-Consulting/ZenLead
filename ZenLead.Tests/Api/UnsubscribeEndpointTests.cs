using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

public class UnsubscribeApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:UnsubscribePermitLimit", "10000");
    }
}

public class UnsubscribeRateLimitedApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:UnsubscribePermitLimit", "2");
    }
}

public class UnsubscribeRateLimitTests(UnsubscribeRateLimitedApiFactory factory) : IClassFixture<UnsubscribeRateLimitedApiFactory>
{
    [Fact]
    public async Task Unsubscribe_is_rate_limited_per_ip()
    {
        var client = factory.ClientFor(null);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/unsubscribe/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/v1/unsubscribe/nope", null)).StatusCode);
    }
}

/// <summary>F23.4: the public unsubscribe endpoints end to end (anonymous; the signed token is the credential).</summary>
public class UnsubscribeEndpointTests(UnsubscribeApiFactory factory) : IClassFixture<UnsubscribeApiFactory>
{
    private sealed record Seeded(Guid WorkspaceId, Lead Lead, Guid[] EnrollmentIds, string Token);

    /// <summary>A lead enrolled (Active) in two campaigns of a fresh workspace, and its signed unsubscribe token.</summary>
    private async Task<Seeded> SeedAsync(bool deleted = false)
    {
        var (workspaceId, _, _) = await factory.CreateWorkspaceAndUserAsync($"Unsub {Guid.NewGuid():N}", $"owner-{Guid.NewGuid():N}@zenlead.test");
        var now = DateTime.UtcNow;
        var lead = new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane Doe", Email = $"jane.{Guid.NewGuid():N}@acme.test",
            Status = LeadStatus.Contacted, CreatedAt = now, DeletedAt = deleted ? now : null
        };
        var enrollmentIds = new Guid[2];
        await factory.WithDbAsync(async db =>
        {
            db.Leads.Add(lead);
            for (var i = 0; i < 2; i++)
            {
                var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = $"C{i}", Status = CampaignStatus.Active, CreatedAt = now };
                db.Campaigns.Add(campaign);
                enrollmentIds[i] = Guid.NewGuid();
                db.CampaignEnrollments.Add(new CampaignEnrollment
                {
                    Id = enrollmentIds[i], WorkspaceId = workspaceId, CampaignId = campaign.Id, LeadId = lead.Id,
                    Status = EnrollmentStatus.Active, NextSendAt = now.AddDays(1), EnrolledAt = now
                });
            }
            return await db.SaveChangesAsync();
        });
        var token = factory.Services.GetRequiredService<IUnsubscribeTokens>().Create(workspaceId, lead.Id);
        return new Seeded(workspaceId, lead, enrollmentIds, token);
    }

    private Task<Lead> LeadOf(Guid id) => factory.WithDbAsync(db => db.Leads.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == id));

    private Task<List<CampaignEnrollment>> EnrollmentsOf(Guid leadId)
        => factory.WithDbAsync(db => db.CampaignEnrollments.IgnoreQueryFilters().AsNoTracking().Where(e => e.LeadId == leadId).ToListAsync());

    private Task<List<SuppressedEmail>> SuppressionsOf(Guid workspaceId)
        => factory.WithDbAsync(db => db.SuppressedEmails.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync());

    [Fact]
    public async Task Post_unsubscribes_suppresses_and_stops_every_enrollment_and_is_idempotent()
    {
        var s = await SeedAsync();
        var client = factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/unsubscribe/{s.Token}", null)).StatusCode);

        Assert.Equal(LeadStatus.Unsubscribed, (await LeadOf(s.Lead.Id)).Status);
        var suppression = Assert.Single(await SuppressionsOf(s.WorkspaceId));
        Assert.Equal(s.Lead.Email, suppression.Email);
        Assert.Equal(SuppressionReason.Unsubscribed, suppression.Reason);
        Assert.Equal(s.Lead.Id, suppression.SourceLeadId);
        Assert.All(await EnrollmentsOf(s.Lead.Id), e =>
        {
            Assert.Equal(EnrollmentStatus.Unsubscribed, e.Status);
            Assert.Null(e.NextSendAt);
        });

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/unsubscribe/{s.Token}", null)).StatusCode);
        Assert.Single(await SuppressionsOf(s.WorkspaceId));
    }

    [Fact]
    public async Task Get_shows_the_masked_address_and_never_changes_anything()
    {
        var s = await SeedAsync();
        var client = factory.ClientFor(null);

        var info = await client.GetFromJsonAsync<UnsubscribeInfoResponse>($"/api/v1/unsubscribe/{s.Token}");

        Assert.Equal($"j***@acme.test", info!.Email);
        Assert.False(info.AlreadyUnsubscribed);
        Assert.Equal(LeadStatus.Contacted, (await LeadOf(s.Lead.Id)).Status);
        Assert.Empty(await SuppressionsOf(s.WorkspaceId));
        Assert.All(await EnrollmentsOf(s.Lead.Id), e => Assert.Equal(EnrollmentStatus.Active, e.Status));

        await client.PostAsync($"/api/v1/unsubscribe/{s.Token}", null);
        Assert.True((await client.GetFromJsonAsync<UnsubscribeInfoResponse>($"/api/v1/unsubscribe/{s.Token}"))!.AlreadyUnsubscribed);
    }

    [Fact]
    public async Task One_click_form_post_from_a_mail_client_works()
    {
        var s = await SeedAsync();
        var body = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]);

        var response = await factory.ClientFor(null).PostAsync($"/api/v1/unsubscribe/{s.Token}", body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(LeadStatus.Unsubscribed, (await LeadOf(s.Lead.Id)).Status);
    }

    [Fact]
    public async Task Works_for_a_soft_deleted_lead()
    {
        var s = await SeedAsync(deleted: true);

        Assert.Equal(HttpStatusCode.NoContent, (await factory.ClientFor(null).PostAsync($"/api/v1/unsubscribe/{s.Token}", null)).StatusCode);
        Assert.Single(await SuppressionsOf(s.WorkspaceId));
    }

    [Fact]
    public async Task Bad_tampered_and_mismatched_tokens_all_get_the_same_404_as_an_unknown_lead()
    {
        var s = await SeedAsync();
        var other = await SeedAsync();
        var tokens = factory.Services.GetRequiredService<IUnsubscribeTokens>();
        var client = factory.ClientFor(null);

        var unknownLead = tokens.Create(s.WorkspaceId, Guid.NewGuid());
        var otherWorkspacesLead = tokens.Create(s.WorkspaceId, other.Lead.Id);                  // validly signed, wrong pairing
        var tampered = s.Token[..^2] + (s.Token[^2] == 'A' ? "BB" : "AA");
        string[] bad = [unknownLead, otherWorkspacesLead, tampered, "not-a-token", new string('x', 300)];

        var reference = await client.PostAsync($"/api/v1/unsubscribe/{unknownLead}", null);
        var referenceBody = await reference.Content.ReadAsStringAsync();
        foreach (var token in bad)
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            {
                var response = await client.SendAsync(new HttpRequestMessage(method, $"/api/v1/unsubscribe/{token}"));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal(referenceBody, await response.Content.ReadAsStringAsync());
            }

        Assert.Empty(await SuppressionsOf(s.WorkspaceId));
        Assert.Equal(LeadStatus.Contacted, (await LeadOf(other.Lead.Id)).Status);
    }

    [Theory]
    [InlineData("jane.doe@acme.com", "j***@acme.com")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("broken", "***")]
    public void Mask_hides_the_local_part(string email, string masked) => Assert.Equal(masked, UnsubscribeUseCase.Mask(email));
}
