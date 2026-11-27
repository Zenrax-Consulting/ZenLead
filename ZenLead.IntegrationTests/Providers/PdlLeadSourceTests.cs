using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Discovery;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.LeadSources;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Providers;

/// <summary>
/// Lead discovery (F14) with the real <see cref="PdlLeadSource"/> against a WireMock PDL: scroll-token paging, the request it sends,
/// and a full run started through the API, enqueued in Hangfire on SQL Server and processed by a real job server. Failures back off
/// and end the run as Failed with a reason; people without a work email are dropped; credits used = records returned.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class PdlLeadSourceTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    private static readonly LeadSearchCriteria Criteria = new(["Head of Operations"], ["Logistics"], ["United Kingdom"], 51, 200, []);

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_FollowsTheScrollToken_AndSendsTheQueryAndKey()
    {
        var (first, second) = await factory.InScopeAsync(async sp =>
        {
            var source = sp.GetRequiredService<ILeadSource>();
            Assert.IsType<PdlLeadSource>(source);
            var page1 = await source.SearchAsync(Criteria, null, 25, CancellationToken.None);
            var page2 = await source.SearchAsync(Criteria, page1.NextCursor, 25, CancellationToken.None);
            return (page1, page2);
        });

        Assert.Equal(PdlStub.SecondPageToken, first.NextCursor);
        Assert.Equal(3, first.Leads.Count);
        Assert.Equal(3, first.CreditsUsed);
        Assert.Null(first.Leads.Single(l => l.ProviderId == "pdl-2").Email);
        Assert.Null(second.NextCursor);
        Assert.Equal(["pdl-4", "pdl-5"], second.Leads.Select(l => l.ProviderId));

        var requests = factory.Stubs.Pdl.Searches;
        Assert.Equal(2, requests.Count);
        Assert.Equal("pdl-integration-test-not-a-key", requests[0].RequestMessage!.Headers!["X-Api-Key"].Single());
        var body = StubServer.BodyJson(requests[0]);
        Assert.Equal(25, body.GetProperty("size").GetInt32());
        Assert.False(body.TryGetProperty("scroll_token", out _));
        Assert.Contains("\"work_email\"", body.GetProperty("query").GetRawText());      // never pay for rows we can't email
        Assert.Contains("\"51-200\"", body.GetProperty("query").GetRawText());
        Assert.Equal(PdlStub.SecondPageToken, StubServer.BodyJson(requests[1]).GetProperty("scroll_token").GetString());
    }

    [Fact]
    public async Task ARunStartedThroughTheApi_IsProcessedByHangfire_DroppingNoEmailRows_AndCountingCredits()
    {
        var (workspaceId, run) = await StartRunAsync(maxLeads: 10);

        await using (var server = HangfireTestServer.For(factory.Services))
            await server.WaitForIdleAsync();

        var finished = await GetRunAsync(workspaceId, run.Id);
        Assert.Equal(DiscoveryRunStatus.Completed, finished.Status);
        Assert.Equal(5, finished.FoundCount);
        Assert.Equal(4, finished.ImportedCount);
        Assert.Equal(1, finished.NoEmailCount);
        Assert.Equal(5, finished.CreditsUsed);                   // one credit per record returned, emailable or not
        await using var db = factory.Sql.CreateContext();
        var leads = await db.Leads.IgnoreQueryFilters().Where(l => l.WorkspaceId == workspaceId).ToListAsync();
        Assert.Equal(4, leads.Count);
        Assert.All(leads, l => Assert.Equal(LeadSource.Discovery, l.Source));
        Assert.All(leads, l => Assert.Equal(run.Id, l.SourceRunId));
        Assert.DoesNotContain(leads, l => l.Name.StartsWith("Ben", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutOfCredits402_FailsTheRunAtOnce_WithAReason()
    {
        factory.Stubs.Pdl.RespondWith(HttpStatusCode.PaymentRequired);
        var (workspaceId, run) = await StartRunAsync(maxLeads: 10);

        await using (var server = HangfireTestServer.For(factory.Services))
            await server.WaitForIdleAsync();

        var finished = await GetRunAsync(workspaceId, run.Id);
        Assert.Equal(DiscoveryRunStatus.Failed, finished.Status);
        Assert.Equal("Provider credits exhausted.", finished.FailureReason);
        Assert.Single(factory.Stubs.Pdl.Searches);               // not transient: no retry
        Assert.Empty(factory.DiscoveryRetryDelays);
    }

    [Fact]
    public async Task RateLimit429_BacksOffThreeTimes_ThenFailsTheRunWithAReason()
    {
        factory.Stubs.Pdl.RespondWith(HttpStatusCode.TooManyRequests);
        var (workspaceId, run) = await StartRunAsync(maxLeads: 10);

        await using (var server = HangfireTestServer.For(factory.Services))
            await server.WaitForIdleAsync();

        var finished = await GetRunAsync(workspaceId, run.Id);
        Assert.Equal(DiscoveryRunStatus.Failed, finished.Status);
        Assert.Equal("Provider rate limit — try again later.", finished.FailureReason);
        Assert.Equal(4, factory.Stubs.Pdl.Searches.Count);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30)], factory.DiscoveryRetryDelays);
        Assert.Equal(0, finished.CreditsUsed);
    }

    private async Task<(Guid WorkspaceId, DiscoveryRunResponse Run)> StartRunAsync(int maxLeads)
    {
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync();
        var client = factory.ClientFor(token);
        var profile = await (await client.PostJsonAsync("/api/v1/target-profiles", new TargetProfileRequest("Ops leaders",
            new CriteriaDto(Criteria.JobTitles, Criteria.Industries, Criteria.Countries, Criteria.CompanySizeMin, Criteria.CompanySizeMax, [])))).ReadAsync<TargetProfileResponse>();
        var started = await client.PostJsonAsync($"/api/v1/target-profiles/{profile.Id}/runs", new StartRunRequest(maxLeads));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        return (workspaceId, await started.ReadAsync<DiscoveryRunResponse>());
    }

    private async Task<DiscoveryRunResponse> GetRunAsync(Guid workspaceId, Guid runId)
    {
        await using var db = factory.Sql.CreateContext();
        return DiscoveryRunResponse.From(await db.DiscoveryRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == runId && r.WorkspaceId == workspaceId));
    }
}
