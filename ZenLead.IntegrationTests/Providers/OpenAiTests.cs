using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Providers;

/// <summary>
/// The real Semantic Kernel OpenAI connector against a WireMock OpenAI (P1 compose, F23 personalisation, F26 classification): token
/// usage lands in <c>AiUsageLog</c> with the right purpose and links, a timeout is retried once, an unusable answer is still logged as
/// spend, and an exhausted budget never reaches the provider.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class OpenAiTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    private Guid _workspaceId;
    private Lead _lead = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync();
        _workspaceId = workspaceId;
        _client = factory.ClientFor(token);
        await using var db = factory.Sql.CreateContext();
        var company = await new CompanyBuilder(workspaceId).Named("Acme").InIndustry("Logistics").InCountry("UK").SaveAsync(db);
        _lead = await new LeadBuilder(workspaceId).Named("Ada Lovelace").WithTitle("CTO").AtCompany(company).SaveAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> ComposeAsync() => _client.PostJsonAsync("/api/v1/ai/compose-email", new ComposeEmailRequest(_lead.Id, "We help logistics teams."));

    [Fact]
    public async Task Compose_ParsesTheAnswer_AndLogsUsageAsCompose()
    {
        var result = await (await ComposeAsync()).ReadAsync<ComposeEmailResult>();

        Assert.Equal(OpenAiStub.DefaultSubject, result.Subject);
        Assert.Equal(OpenAiStub.DefaultBody, result.Body);
        Assert.Equal(OpenAiStub.DefaultPromptTokens + OpenAiStub.DefaultCompletionTokens, result.TokensUsed);

        var call = Assert.Single(factory.Stubs.OpenAi.ChatCalls);
        Assert.Equal("Bearer sk-integration-test-not-a-key", call.RequestMessage!.Headers!["Authorization"].Single());
        var body = StubServer.BodyJson(call);
        Assert.Equal("gpt-4o", body.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Contains("Company: Acme", ContentOf(messages[1]));

        var log = Assert.Single(await UsageAsync());
        Assert.Equal(AiPurpose.Compose, log.Purpose);
        Assert.Equal(OpenAiStub.DefaultPromptTokens, log.PromptTokens);
        Assert.Equal(OpenAiStub.DefaultCompletionTokens, log.CompletionTokens);
        Assert.True(log.Succeeded);
        Assert.True(log.EstimatedCostUsd > 0);
    }

    [Fact]
    public async Task Compose_TimesOutOnce_ThenRetries_AndSucceeds()
    {
        factory.Stubs.OpenAi.TimeOutOnceThenReplyWith(OpenAiStub.ComposeJson("After retry", "Second attempt body."));

        var result = await (await ComposeAsync()).ReadAsync<ComposeEmailResult>();

        Assert.Equal("After retry", result.Subject);
        Assert.Equal(2, (await factory.Stubs.OpenAi.WaitForRequestsAsync(OpenAiStub.ChatPath, 2)).Count);
        Assert.Single(await UsageAsync());                         // only the call that answered is billed
    }

    [Fact]
    public async Task Compose_WithMalformedJson_Is502_AndTheBilledTokensAreStillLogged()
    {
        factory.Stubs.OpenAi.ReplyWith("Sure! Here is your email: Subject - Hi", promptTokens: 90, completionTokens: 30);

        var response = await ComposeAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var log = Assert.Single(await UsageAsync());
        Assert.False(log.Succeeded);
        Assert.Equal(90, log.PromptTokens);
        Assert.Equal(30, log.CompletionTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    public async Task Compose_WhenTheProviderFails_MapsTheStatus_AndLogsNoSpend(HttpStatusCode provider, HttpStatusCode expected)
    {
        factory.Stubs.OpenAi.RespondWith(provider);

        var response = await ComposeAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.Single(factory.Stubs.OpenAi.ChatCalls);            // no hidden SDK retries against a failing provider
        Assert.Empty(await UsageAsync());
    }

    [Fact]
    public async Task Compose_OverTheBudget_IsRefusedBeforeAnyProviderCall()
    {
        await using (var db = factory.Sql.CreateContext())
        {
            db.AiUsageLogs.Add(new AiUsageLog
            {
                Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Model = "gpt-4o", PromptTokens = 1, CompletionTokens = 1,
                EstimatedCostUsd = 1_000m, CreatedAt = factory.Clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync();
        }

        var response = await ComposeAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("ai_budget_exceeded", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Stubs.OpenAi.ChatCalls);
    }

    [Fact]
    public async Task Personalisation_UsesTheModelsDraft_AndLogsUsageAgainstTheCampaignAndEnrollment()
    {
        factory.Stubs.OpenAi.ReplyWith(OpenAiStub.ComposeJson("Ada, a thought on Acme's routing", "Hi Ada, personalised body."));
        DueSend due;
        await using (var db = factory.Sql.CreateContext())
            due = await SendingSeed.DueEnrollmentAsync(db, factory.Clock.GetUtcNow().UtcDateTime, aiPersonalisation: true);

        var summary = await factory.InScopeAsync(sp => sp.GetRequiredService<SendDueEmailsUseCase>().ExecuteAsync(CancellationToken.None));

        Assert.Equal(1, summary.Sent);
        var sent = StubServer.BodyJson(Assert.Single(factory.Stubs.SendGrid.MailSends));
        Assert.Equal("Ada, a thought on Acme's routing", sent.GetProperty("subject").GetString());
        var log = Assert.Single(await UsageAsync(due.Workspace.Id));
        Assert.Equal(AiPurpose.Personalise, log.Purpose);
        Assert.Equal(due.Campaign.Id, log.CampaignId);
        Assert.Equal(due.Enrollment.Id, log.EnrollmentId);
        await using var check = factory.Sql.CreateContext();
        Assert.True((await check.EmailMessages.IgnoreQueryFilters().SingleAsync(m => m.EnrollmentId == due.Enrollment.Id)).UsedAi);
    }

    [Fact]
    public async Task Personalisation_WhenTheModelFails_SendsThePlainTemplate()
    {
        factory.Stubs.OpenAi.RespondWith(HttpStatusCode.InternalServerError);
        DueSend due;
        await using (var db = factory.Sql.CreateContext())
            due = await SendingSeed.DueEnrollmentAsync(db, factory.Clock.GetUtcNow().UtcDateTime, aiPersonalisation: true);

        var summary = await factory.InScopeAsync(sp => sp.GetRequiredService<SendDueEmailsUseCase>().ExecuteAsync(CancellationToken.None));

        Assert.Equal(1, summary.Sent);
        Assert.Equal("Hello Ada", StubServer.BodyJson(Assert.Single(factory.Stubs.SendGrid.MailSends)).GetProperty("subject").GetString());
        Assert.Empty(await UsageAsync(due.Workspace.Id));
    }

    [Fact]
    public async Task Classification_RunsAsAHangfireJob_WithTheClassifierModel_AndLogsUsageAsClassifyReply()
    {
        factory.Stubs.OpenAi.ReplyWith(OpenAiStub.ClassifyJson("interested", 0.93m), promptTokens: 300, completionTokens: 12);
        DueSend due;
        InboxMessage reply;
        await using (var db = factory.Sql.CreateContext())
        {
            due = await SendingSeed.DueEnrollmentAsync(db, factory.Clock.GetUtcNow().UtcDateTime);
            var thread = new InboxThreadBuilder(due.Lead, due.Enrollment).WithReply("Yes, send me pricing please.");
            await thread.SaveAsync(db);
            reply = thread.Messages.Single(m => m.Direction == MessageDirection.Inbound);
        }

        // enqueued through the app's own scheduler (HangfireJobScheduler → SQL Server storage), run by a real Hangfire server
        await factory.InScopeAsync(sp =>
        {
            sp.GetRequiredService<IJobScheduler>().EnqueueReplyClassification(due.Workspace.Id, reply.Id);
            return Task.CompletedTask;
        });
        await using (var server = HangfireTestServer.For(factory.Services))
            await server.WaitForIdleAsync();

        await using var check = factory.Sql.CreateContext();
        var classified = await check.InboxMessages.IgnoreQueryFilters().SingleAsync(m => m.Id == reply.Id);
        Assert.Equal(ReplyClassification.Interested, classified.Classification);
        Assert.Equal(ClassificationSource.Ai, classified.ClassifiedBy);
        Assert.Equal(0.93m, classified.ClassificationConfidence);

        var body = StubServer.BodyJson(Assert.Single(factory.Stubs.OpenAi.ChatCalls));
        Assert.Equal("gpt-4o-mini", body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());
        Assert.Contains("Yes, send me pricing please.", ContentOf(body.GetProperty("messages")[1]));

        var log = Assert.Single(await UsageAsync(due.Workspace.Id));
        Assert.Equal(AiPurpose.ClassifyReply, log.Purpose);
        Assert.Equal(due.Campaign.Id, log.CampaignId);
        Assert.Equal(due.Enrollment.Id, log.EnrollmentId);
        Assert.Equal(300, log.PromptTokens);
    }

    private static string ContentOf(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""));
    }

    private async Task<List<AiUsageLog>> UsageAsync(Guid? workspaceId = null)
    {
        await using var db = factory.Sql.CreateContext();
        var ws = workspaceId ?? _workspaceId;
        return await db.AiUsageLogs.IgnoreQueryFilters().Where(l => l.WorkspaceId == ws).ToListAsync();
    }
}
