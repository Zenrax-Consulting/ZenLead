# Feature 9 — Cost Logging & AI Test Coverage

**Branch:** `feature/cost-logging-polish`
**Milestone:** 2 — AI loop
**Depends on:** Feature 6 (`ComposeEmailUseCase`, `EmailComposer` prompt logic to test).

## Goal
Prompt-building and JSON-shape parsing have unit coverage against a fake `IEmailComposer` (no real OpenAI calls in CI), and there's a quick, local way to sanity-check cumulative token spend before Milestone 3.

## Files to add/modify

### `ZenLead.Infrastructure.csproj` (modified — expose internals to tests)
```xml
<ItemGroup>
  <InternalsVisibleTo Include="ZenLead.Tests" />
</ItemGroup>
```
Needed so `EmailComposer`'s prompt-building helper and `SubjectBodyDto` (both `internal`) are testable without making them public API.

### `ZenLead.Infrastructure/Ai/EmailComposer.cs` (modified — `BuildUserMessage` becomes `internal static` for testability)
```csharp
internal static string BuildUserMessage(EmailComposeContext context) =>
    $"""
    Lead name: {context.LeadName}
    Lead email: {context.LeadEmail}
    Lead title: {context.LeadTitle ?? "unknown"}
    Additional context: {context.AdditionalContext ?? "none"}
    """;
```
(Same body as Feature 6 — only the access modifier changes, from `private` to `internal`.) Also add, right after logging token usage in `ComposeAsync`:
```csharp
TokenUsageTracker.Add(tokensUsed);
```

### `ZenLead.Infrastructure/Ai/TokenUsageTracker.cs` (new)
```csharp
namespace ZenLead.Infrastructure.Ai;

public static class TokenUsageTracker
{
    private static long _totalTokens;

    public static void Add(int tokens) => Interlocked.Add(ref _totalTokens, tokens);
    public static long Total => Interlocked.Read(ref _totalTokens);
}
```
A process-lifetime counter, not persisted — good enough for "sanity-check against the ~$7–14 budget before Milestone 3" per the parent plan. It's a rough proxy, not authoritative; Feature 10's spend check still means looking at the real OpenAI dashboard, not trusting this number.

### `ZenLead.Api/Controllers/V1/AiController.cs` (modified — add a dev-convenience endpoint)
```csharp
[HttpGet("token-usage")]
public ActionResult<object> GetTokenUsage() => Ok(new { totalTokens = TokenUsageTracker.Total });
```
(Add `using ZenLead.Infrastructure.Ai;` to the controller's usings.)

### `ZenLead.Tests/Infrastructure/Ai/EmailComposerPromptTests.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

public class EmailComposerPromptTests
{
    [Fact]
    public void BuildUserMessage_IncludesLeadFields()
    {
        var context = new EmailComposeContext("Jane Doe", "jane@acme.com", "VP Sales", "Met at conference");

        var message = EmailComposer.BuildUserMessage(context);

        Assert.Contains("Jane Doe", message);
        Assert.Contains("jane@acme.com", message);
        Assert.Contains("VP Sales", message);
        Assert.Contains("Met at conference", message);
    }
}
```

### `ZenLead.Tests/Infrastructure/Ai/SubjectBodyDtoParsingTests.cs` (new)
```csharp
using System.Text.Json;
using ZenLead.Infrastructure.Ai;

namespace ZenLead.Tests.Infrastructure.Ai;

public class SubjectBodyDtoParsingTests
{
    [Fact]
    public void Deserialize_ValidJson_ProducesExpectedShape()
    {
        var json = """{ "Subject": "Quick question", "Body": "Hi Jane, ..." }""";

        var result = JsonSerializer.Deserialize<SubjectBodyDto>(json);

        Assert.NotNull(result);
        Assert.Equal("Quick question", result!.Subject);
        Assert.Equal("Hi Jane, ...", result.Body);
    }

    [Fact]
    public void Deserialize_MissingField_LeavesItNull()
    {
        // Documents current behavior: System.Text.Json doesn't enforce non-null record
        // parameters on deserialize, so a malformed model response silently produces a
        // null Body rather than throwing. If EmailComposer.ComposeAsync should instead
        // reject this, add that validation explicitly — don't rely on the deserializer.
        var json = """{ "Subject": "Quick question" }""";

        var result = JsonSerializer.Deserialize<SubjectBodyDto>(json);

        Assert.NotNull(result);
        Assert.Null(result!.Body);
    }
}
```

### `ZenLead.Tests/Application/Ai/Fakes.cs` (new)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Tests.Application.Ai;

public class FakeEmailComposer : IEmailComposer
{
    public ComposedEmail Response { get; set; } = new("Subject", "Body", 42);
    public EmailComposeContext? LastContext { get; private set; }

    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        LastContext = context;
        return Task.FromResult(Response);
    }
}

public class FakeLeadRepositoryForAi : ILeadRepository
{
    private readonly Dictionary<Guid, Lead> _leads = [];

    public void Seed(Lead lead) => _leads[lead.Id] = lead;

    public Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default)
    {
        _leads[lead.Id] = lead;
        return Task.FromResult(lead);
    }

    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_leads.GetValueOrDefault(id));

    public Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Lead>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList());
}
```

### `ZenLead.Tests/Application/Ai/ComposeEmailUseCaseTests.cs` (new)
```csharp
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Ai;

public class ComposeEmailUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_LeadInCallersWorkspace_ReturnsComposedEmail()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var composer = new FakeEmailComposer();
        var sut = new ComposeEmailUseCase(leads, composer);

        var result = await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, "context"), workspaceId);

        Assert.NotNull(result);
        Assert.Equal("Subject", result!.Subject);
        Assert.Equal("Jane", composer.LastContext!.LeadName);
    }

    [Fact]
    public async Task ExecuteAsync_LeadInDifferentWorkspace_ReturnsNull()
    {
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var sut = new ComposeEmailUseCase(leads, new FakeEmailComposer());

        var result = await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), Guid.NewGuid());

        Assert.Null(result);
    }
}
```

### `ZenLead.Tests/Api/AiControllerTests.cs` (new)
`LeadsController` got a direct controller unit test for its workspace-scoping branch (Feature 10). `AiController` never got the equivalent for its 404 (lead not found) and 504 (composer timeout) branches — this closes that gap, reusing the `FakeLeadRepositoryForAi`/`FakeEmailComposer` fakes above plus one more fake that simulates the `EmailComposer` timeout path.
```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Ai;

namespace ZenLead.Tests.Api;

internal class ThrowingEmailComposer : IEmailComposer
{
    public Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
        => throw new OperationCanceledException("Simulated timeout");
}

public class AiControllerTests
{
    private static AiController BuildController(ComposeEmailUseCase useCase, Guid callerWorkspaceId)
    {
        var controller = new AiController(useCase);
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task ComposeEmail_LeadNotFoundInCallersWorkspace_ReturnsNotFound()
    {
        var useCase = new ComposeEmailUseCase(new FakeLeadRepositoryForAi(), new FakeEmailComposer());
        var controller = BuildController(useCase, Guid.NewGuid());

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(Guid.NewGuid(), null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ComposeEmail_LeadFound_ReturnsComposedEmail()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var useCase = new ComposeEmailUseCase(leads, new FakeEmailComposer());
        var controller = BuildController(useCase, workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Subject", Assert.IsType<ComposeEmailApiResponse>(ok.Value).Subject);
    }

    [Fact]
    public async Task ComposeEmail_ComposerTimesOut_Returns504()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var useCase = new ComposeEmailUseCase(leads, new ThrowingEmailComposer());
        var controller = BuildController(useCase, workspaceId);

        var result = await controller.ComposeEmail(new ComposeEmailApiRequest(lead.Id, null), CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, statusResult.StatusCode);
    }
}
```
`CancellationToken.None` is passed as the request's `ct`, so `AiController`'s `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` guard evaluates true (the caller never cancelled) and the 504 branch runs — exercising the same condition that distinguishes "OpenAI timed out" from "the caller gave up."

## Verification
- `dotnet test ZenLead.Tests` — all new tests pass, no real OpenAI calls made.
- `GET /api/v1/ai/token-usage` returns a growing number across repeated real `compose-email` calls in a dev session.
