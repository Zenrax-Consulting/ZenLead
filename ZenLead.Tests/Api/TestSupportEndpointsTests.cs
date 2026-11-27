using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Infrastructure.Testing;
using ZenLead.Tests.Api.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.Tests.Api;

/// <summary>
/// The E2E environment end to end over SQLite (T00): seeds, pickup mail, recorded jobs and the clock, through the real endpoints and
/// the environment's own registrations (no test fakes swapped in).
/// </summary>
public sealed class TestSupportEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };   // like the API
    private readonly E2EApiFactory _e2e = new();
    private readonly HttpClient _client;

    public TestSupportEndpointsTests() => _client = _e2e.Client();

    public void Dispose() => _e2e.Dispose();

    // ---------- seeds ----------

    [Theory]
    [InlineData("empty")]
    [InlineData("gate2")]
    [InlineData("inbox")]
    [InlineData("analytics")]
    public async Task Seed_RunsTwiceWithoutColliding_AndReturnsWhatItPromises(string scenario)
    {
        var first = await SeedAsync(scenario);
        var second = await SeedAsync(scenario);

        Assert.Equal(E2EApiFactory.SuperAdminEmail, first.SuperAdminEmail);
        Assert.NotEqual(first.RunId, second.RunId);
        if (scenario == "empty")
        {
            Assert.Empty(first.Workspaces);
            return;
        }

        Assert.NotEqual(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(25, first.LeadIds.Count);
        Assert.Equal(3, first.CompanyIds.Count);
        Assert.NotNull(first.TargetProfileId);
        Assert.NotNull(first.DraftCampaignId);
        Assert.Equal(first.WorkspaceId, first.Workspaces["main"].Id);
        Assert.Equal(first.OtherWorkspaceId, first.Workspaces["other"].Id);
        Assert.Empty(first.LeadIds.Intersect(second.LeadIds));

        if (scenario is "inbox" or "analytics")
        {
            Assert.Equal(["interested", "needsReview", "unsubscribe"], first.ThreadIds.Keys.Order());
            Assert.Equal(2, first.SentEnrollmentIds.Count);
            Assert.NotNull(first.QuarantineId);
        }
        if (scenario == "analytics")
        {
            Assert.NotNull(first.AnalyticsCampaignId);
            Assert.NotNull(first.DstChangeInAnalyticsRange);
        }

        // what was returned is what is in the database, in the right workspace
        await _e2e.WithScopeAsync(async db =>
        {
            var ws = first.WorkspaceId!.Value;
            Assert.Equal(25, await db.Leads.IgnoreQueryFilters().CountAsync(l => l.WorkspaceId == ws));
            Assert.Equal(1, await db.Leads.IgnoreQueryFilters().CountAsync(l => l.WorkspaceId == first.OtherWorkspaceId));
            Assert.Equal(3, await db.CampaignSteps.IgnoreQueryFilters().CountAsync(s => s.CampaignId == first.DraftCampaignId));
            foreach (var threadId in first.ThreadIds.Values)
                Assert.Equal(ws, (await db.InboxThreads.IgnoreQueryFilters().SingleAsync(t => t.Id == threadId)).WorkspaceId);
            if (scenario == "analytics")
                Assert.Equal(20, await db.EmailMessages.IgnoreQueryFilters().CountAsync(m => m.CampaignId == first.AnalyticsCampaignId && m.SentAt != null));
        });
    }

    [Fact]
    public async Task Seed_ReturnedCredentials_SignIn()
    {
        var seed = await SeedAsync("gate2");
        foreach (var ws in seed.Workspaces.Values)
        {
            var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.AdminEmail, password = ws.AdminPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task Seed_IsolatesTheTwoWorkspaces()
    {
        var seed = await SeedAsync("gate2");
        var other = seed.Workspaces["other"];
        var login = await (await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = other.AdminEmail, password = other.AdminPassword }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var client = _e2e.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());

        var response = await client.GetAsync($"/api/v1/leads/{seed.LeadIds[0]}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Seed_UnknownScenario_Is404()
        => Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/v1/test/seed/everything", null)).StatusCode);

    // ---------- pickup mail ----------

    [Fact]
    public async Task Register_WritesTheApprovalEmailToThePickupFolder()
    {
        var seed = await SeedAsync("gate2");
        var response = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"newbie-{seed.RunId}@example.test", password = ApiFactory.NewPassword(), displayName = "Newbie", workspaceId = seed.WorkspaceId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var file = Assert.Single(Directory.GetFiles(_e2e.PickupDirectory, "*.json"));
        using var mail = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(seed.Workspaces["main"].AdminEmail, mail.RootElement.GetProperty("to").GetProperty("address").GetString());
        Assert.Contains("/approve?token=", mail.RootElement.GetProperty("text").GetString());
    }

    // ---------- recorded jobs ----------

    [Fact]
    public async Task RunPending_ClassifiesThroughTheRealJob_WithTheFakeClassifier_AndLogsUsage()
    {
        var seed = await SeedAsync("gate2");
        var (interested, unsubscribe) = await _e2e.WithScopeAsync(async db =>
        {
            var ws = seed.WorkspaceId!.Value;
            var leads = await db.Leads.IgnoreQueryFilters().Where(l => l.WorkspaceId == ws && l.Status == LeadStatus.New).Take(2).ToListAsync();
            var a = new InboxThreadBuilder(leads[0]).WithReply("Sounds good, call me");
            var b = new InboxThreadBuilder(leads[1]).WithReply("Please [[unsubscribe]] me");
            await a.SaveAsync(db);
            await b.SaveAsync(db);
            return (a.Messages[^1].Id, b.Messages[^1].Id);
        });
        var queue = _e2e.Services.GetRequiredService<RecordedJobQueue>();
        var now = DateTimeOffset.UtcNow;
        queue.Add(RecordedJobType.Classify, interested, seed.WorkspaceId!.Value, now);
        queue.Add(RecordedJobType.Classify, unsubscribe, seed.WorkspaceId!.Value, now);

        var pending = await _client.GetFromJsonAsync<List<RecordedJob>>("/api/v1/test/jobs", Web);
        Assert.Equal(2, pending!.Count);

        var run = await (await _client.PostAsync("/api/v1/test/jobs/run-pending", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, run.GetProperty("ran").GetInt32());
        Assert.True(run.GetProperty("failed").GetInt32() == 0, run.ToString());
        Assert.Empty(queue.Pending);

        await _e2e.WithScopeAsync(async db =>
        {
            var messages = await db.InboxMessages.IgnoreQueryFilters().Where(m => m.Id == interested || m.Id == unsubscribe).ToDictionaryAsync(m => m.Id);
            Assert.Equal(ReplyClassification.Interested, messages[interested].Classification);
            Assert.Equal(ReplyClassification.Unsubscribe, messages[unsubscribe].Classification);
            Assert.Equal(2, await db.AiUsageLogs.IgnoreQueryFilters().CountAsync(u => u.WorkspaceId == seed.WorkspaceId && u.Purpose == AiPurpose.ClassifyReply));
            return 0;
        });
    }

    [Fact]
    public async Task RunPending_AFailingJob_IsReported_AndTheOthersStillRun()
    {
        // every CSV repository call throws, so the CSV job fails the way an unexpected error would
        using var e2e = new E2EApiFactory(s =>
        {
            s.RemoveAll<ICsvImportRepository>();
            s.AddScoped(_ => ThrowingProxy<ICsvImportRepository>.Create());
        });
        var client = e2e.Client();
        var seed = (await (await client.PostAsync("/api/v1/test/seed/gate2", null)).Content.ReadFromJsonAsync<SeedResultDto>(Web))!;
        var messageId = await e2e.WithScopeAsync(async db =>
        {
            var lead = await db.Leads.IgnoreQueryFilters().FirstAsync(l => l.WorkspaceId == seed.WorkspaceId && l.Status == LeadStatus.New);
            var thread = new InboxThreadBuilder(lead).WithReply("Tell me more");
            await thread.SaveAsync(db);
            return thread.Messages[^1].Id;
        });
        var queue = e2e.Services.GetRequiredService<RecordedJobQueue>();
        queue.Add(RecordedJobType.Csv, Guid.NewGuid(), seed.WorkspaceId!.Value, DateTimeOffset.UtcNow);
        queue.Add(RecordedJobType.Classify, messageId, seed.WorkspaceId!.Value, DateTimeOffset.UtcNow);

        var run = await (await client.PostAsync("/api/v1/test/jobs/run-pending", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, run.GetProperty("ran").GetInt32());
        Assert.Equal(1, run.GetProperty("failed").GetInt32());
        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.False(results[0].GetProperty("succeeded").GetBoolean());
        Assert.Contains("csv repository unavailable", results[0].GetProperty("error").GetString());
        Assert.True(results[1].GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task RunPending_FilteredByType_LeavesTheRestQueued()
    {
        var queue = _e2e.Services.GetRequiredService<RecordedJobQueue>();
        queue.Add(RecordedJobType.Csv, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        queue.Add(RecordedJobType.Discovery, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        var run = await (await _client.PostAsync("/api/v1/test/jobs/run-pending?type=csv", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, run.GetProperty("ran").GetInt32());
        Assert.Equal(RecordedJobType.Discovery, Assert.Single(queue.Pending).Type);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/v1/test/jobs/run-pending?type=everything", null)).StatusCode);
    }

    [Fact]
    public void EnqueueingThroughTheApp_IsRecorded_NotRun()
    {
        using var scope = _e2e.Services.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<ZenLead.Application.Abstractions.IJobScheduler>();
        Assert.IsType<RecordingJobScheduler>(scheduler);

        var batch = Guid.NewGuid();
        var ws = Guid.NewGuid();
        scheduler.EnqueueCsvImport(batch, ws);
        scheduler.EnqueueDiscoveryRun(Guid.NewGuid(), ws);

        var pending = _e2e.Services.GetRequiredService<RecordedJobQueue>().Pending;
        Assert.Equal([RecordedJobType.Csv, RecordedJobType.Discovery], pending.Select(j => j.Type));
        Assert.Equal(batch, pending[0].Id);
        Assert.True(pending[0].Sequence < pending[1].Sequence);
    }

    [Fact]
    public async Task CampaignSender_RunsOnce_AndMailsADueEnrollmentToThePickupFolder()
    {
        var seed = await SeedAsync("gate2");
        await _e2e.WithScopeAsync(async db =>
        {
            var ws = seed.WorkspaceId!.Value;
            var lead = await db.Leads.IgnoreQueryFilters().FirstAsync(l => l.WorkspaceId == ws && l.Status == LeadStatus.New);
            var campaign = await new CampaignBuilder(ws).Named("Due now").CreatedBy(seed.Workspaces["main"].AdminUserId)
                .WithWindow(new TimeOnly(0, 0), new TimeOnly(23, 59), SendDays.All).Active(DateTime.UtcNow.AddDays(-1)).SaveAsync(db);
            await new EnrollmentBuilder(campaign, lead).DueAt(DateTime.UtcNow.AddMinutes(-5)).SaveAsync(db);
            return 0;
        });

        var response = await _client.PostAsync("/api/v1/test/jobs/campaign-sender/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mail = Assert.Single(Directory.GetFiles(_e2e.PickupDirectory, "*.json"));
        Assert.Contains("@example.test", await File.ReadAllTextAsync(mail));
    }

    // ---------- clock ----------

    [Fact]
    public async Task Clock_AdvanceAndSet_MoveTheAppsTimeProvider()
    {
        var before = await NowAsync("/api/v1/test/clock");
        var advanced = await NowAsync("/api/v1/test/clock/advance", new { minutes = 3 * 24 * 60 });
        Assert.InRange(advanced - before, TimeSpan.FromDays(3), TimeSpan.FromDays(3).Add(TimeSpan.FromMinutes(1)));
        Assert.InRange(_e2e.Services.GetRequiredService<TimeProvider>().GetUtcNow() - advanced, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        var target = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var set = await NowAsync("/api/v1/test/clock/set", new { utc = target });
        Assert.InRange(set - target, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/test/clock/advance", new { minutes = -5 })).StatusCode);
    }

    [Fact]
    public async Task Seed_DatesFollowTheTestClock()
    {
        await NowAsync("/api/v1/test/clock/set", new { utc = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero) });
        var seed = await SeedAsync("analytics");
        Assert.True(seed.DstChangeInAnalyticsRange);         // 11 Oct → 10 Nov crosses the last Sunday of October in London
        Assert.Equal(2026, seed.Now.Year);
        Assert.Equal(11, seed.Now.Month);
    }

    // ---------- helpers ----------

    private async Task<SeedResultDto> SeedAsync(string scenario)
    {
        var response = await _client.PostAsync($"/api/v1/test/seed/{scenario}", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SeedResultDto>(Web))!;
    }

    private async Task<DateTimeOffset> NowAsync(string path, object? body = null)
    {
        var response = body is null ? await _client.GetAsync(path) : await _client.PostAsJsonAsync(path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("now").GetDateTimeOffset();
    }

    /// <summary>The seed response as a test client sees it (JSON, camelCase).</summary>
    private sealed record SeedResultDto(
        string Scenario, string RunId, DateTimeOffset Now, string SuperAdminEmail, Dictionary<string, SeedWorkspace> Workspaces,
        Guid? WorkspaceId, Guid? OtherWorkspaceId, List<Guid> CompanyIds, List<Guid> LeadIds, Guid? TargetProfileId, Guid? DraftCampaignId,
        Guid? InboxCampaignId, Dictionary<string, Guid> ThreadIds, List<Guid> SentEnrollmentIds, Guid? QuarantineId, Guid? AnalyticsCampaignId,
        bool? DstChangeInAnalyticsRange);
}

/// <summary>An implementation of <typeparamref name="T"/> whose every method throws.</summary>
public class ThrowingProxy<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, ThrowingProxy<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("csv repository unavailable (test)");
}

internal static class E2EApiFactoryDbExtensions
{
    public static async Task<T> WithScopeAsync<T>(this E2EApiFactory factory, Func<ZenLeadDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>());
    }

    public static async Task WithScopeAsync(this E2EApiFactory factory, Func<ZenLeadDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>());
    }
}
