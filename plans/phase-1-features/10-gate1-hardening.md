# Feature 10 — Gate 1 Hardening & Demo Readiness

**Branch:** `feature/gate1-hardening`
**Milestone:** 3 — Demo & Gate 1
**Depends on:** Milestones 1 and 2 complete. Per the parent plan's goal for this milestone — "no new functionality" — everything here hardens existing behavior; nothing introduces a new screen or endpoint.

## PBI 10.1 — Auth & UX hardening pass

### `ZenLead.Client/src/app/features/leads/leads-list/leads-list.html` (modified — empty state)
```html
<p *ngIf="!loading && leads.length === 0" class="empty-state">No leads yet — add your first one above.</p>
```
Expired-token handling and duplicate-registration messaging are **already covered** by Feature 3's `AuthInterceptor` (401 → refresh → on failure, logout + redirect to `/login`) and `Register`'s error handler — re-verify both manually rather than re-implementing them; this feature only closes the one gap the parent plan calls out that wasn't already handled: the leads list had no empty state.

### `ZenLead.Tests/Api/LeadsControllerTests.cs` (new — the cross-tenant regression test)
```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Api;

public class FakeLeadRepository : ILeadRepository
{
    private readonly Dictionary<Guid, Lead> _leads = [];
    public void Seed(Lead lead) => _leads[lead.Id] = lead;
    public Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default) { _leads[lead.Id] = lead; return Task.FromResult(lead); }
    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_leads.GetValueOrDefault(id));
    public Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Lead>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList());
}

public class LeadsControllerTests
{
    private static LeadsController BuildController(FakeLeadRepository repo, Guid callerWorkspaceId)
    {
        var controller = new LeadsController(repo);
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task GetById_LeadBelongsToDifferentWorkspace_ReturnsNotFound()
    {
        var repo = new FakeLeadRepository();
        var otherWorkspaceLead = new Lead { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        repo.Seed(otherWorkspaceLead);

        var controller = BuildController(repo, Guid.NewGuid()); // caller is in a *different* workspace

        var result = await controller.GetById(otherWorkspaceLead.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetById_LeadBelongsToCallersWorkspace_ReturnsIt()
    {
        var workspaceId = Guid.NewGuid();
        var repo = new FakeLeadRepository();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        repo.Seed(lead);

        var controller = BuildController(repo, workspaceId);

        var result = await controller.GetById(lead.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(lead.Id, Assert.IsType<LeadResponse>(ok.Value).Id);
    }
}
```
This is a direct controller unit test (constructing `LeadsController` with a fake repository and a hand-built `ClaimsPrincipal`), not a full `WebApplicationFactory` integration test — lighter weight, no new test-hosting package, consistent with the project's existing fake-based testing convention. It proves the manual `workspace_id` claim check actually rejects a cross-tenant lookup; the automatic EF global query filter stays a Sprint 1 item per the parent plan's explicit scope boundary.

## PBI 10.2 — Clean-machine migration reproducibility (no code)
```
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
```
Run against a fresh/dropped LocalDB instance and confirm it comes up clean with no manual intervention — this is a verification step, not a code change.

## PBI 10.3 — Reliability & timing pass

### `ZenLead.Infrastructure/Ai/EmailComposer.cs` (modified — single retry on timeout)
```csharp
ChatMessageContent response;
try
{
    response = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    logger.LogWarning("compose-email call timed out, retrying once for lead {LeadEmail}", context.LeadEmail);
    response = await chat.GetChatMessageContentAsync(history, settings, kernel, ct); // one retry, no backoff — Gate 1 scope only
}
```
This replaces the direct `var response = await chat.GetChatMessageContentAsync(...)` line from Feature 6/7 inside `ComposeAsync`. If this second attempt also times out, it propagates as before and `AiController`'s existing `catch (OperationCanceledException)` still returns the `504`.

### `ZenLead.Tests/Infrastructure/Ai/EmailComposerRetryTests.cs` (new)
Off-by-one bugs in retry logic are easy to introduce and easy to miss by eye — this verifies the retry fires exactly once, not zero or infinite times. Builds a real `Kernel` with a fake `IChatCompletionService` swapped in (Semantic Kernel's `Kernel` is just a thin wrapper over a standard `IServiceProvider`, so this works without touching the network).
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

internal class FakeChatCompletionService : IChatCompletionService
{
    public int CallCount { get; private set; }
    public int ThrowForFirstNCalls { get; set; }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (CallCount <= ThrowForFirstNCalls)
            throw new OperationCanceledException("Simulated timeout");

        var content = new ChatMessageContent(AuthorRole.Assistant, """{ "Subject": "Hi", "Body": "Body text" }""");
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>([content]);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Not used by EmailComposer.");
}

public class EmailComposerRetryTests
{
    private static Kernel BuildKernel(FakeChatCompletionService fakeChat)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(fakeChat);
        return builder.Build();
    }

    [Fact]
    public async Task ComposeAsync_FirstCallTimesOut_RetriesOnceAndSucceeds()
    {
        var fakeChat = new FakeChatCompletionService { ThrowForFirstNCalls = 1 };
        var sut = new EmailComposer(BuildKernel(fakeChat), NullLogger<EmailComposer>.Instance);

        var result = await sut.ComposeAsync(new EmailComposeContext("Jane", "jane@acme.com", null, null));

        Assert.Equal(2, fakeChat.CallCount);
        Assert.Equal("Hi", result.Subject);
    }

    [Fact]
    public async Task ComposeAsync_BothCallsTimeOut_PropagatesException()
    {
        var fakeChat = new FakeChatCompletionService { ThrowForFirstNCalls = 2 };
        var sut = new EmailComposer(BuildKernel(fakeChat), NullLogger<EmailComposer>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ComposeAsync(new EmailComposeContext("Jane", "jane@acme.com", null, null)));

        Assert.Equal(2, fakeChat.CallCount); // exactly one retry, not an infinite loop
    }
}
```

Also (no code): time the full register → add lead → generate draft loop 5–10 times back to back; only add the retry above if timing is actually inconsistent in practice — don't add further retries/backoff speculatively. And verify OpenAI spend to date against the ~$20 Gate 1 ceiling directly in the OpenAI dashboard (not via `TokenUsageTracker`, which is a rough proxy, not authoritative).

## PBI 10.4 — Gate 1 rehearsal (no code)
Dry-run the Gate 1 checklist (parent plan §5) end to end on a fresh browser session / fresh LocalDB, with no manual DB seeding. Capture screenshots or a short recording as the gate-meeting artifact.

## PBI 10.5 — Gate 1 demo & decision (no code)
Live demo against the checklist; record the fund/no-fund outcome and any follow-ups for Sprint 1's backlog.

## Verification
- `dotnet test ZenLead.Tests` — all tests, including the new cross-tenant regression test and the retry-on-timeout tests, pass.
- `ng build` succeeds; the leads list shows the empty-state message against a freshly migrated, lead-free workspace.
- The full register → login → add lead → generate draft loop completes reliably, timed 5–10 times back to back.
- All four [Gate 1 exit criteria](../phase-1-poc-implementation-plan.md#5-gate-1-exit-criteria-unchanged-from-parent-plan-3--restated-as-a-checklist) check out on a fresh environment.
