# Feature 7 — Compose Endpoint

**Branch:** `feature/compose-endpoint`
**Milestone:** 2 — AI loop
**Depends on:** Feature 6 (`ComposeEmailUseCase`).

## Goal
`POST /api/v1/ai/compose-email` is callable, workspace-scoped, and fails fast with a friendly error instead of hanging when OpenAI is slow.

## Files to add/modify

### `ZenLead.Api/Program.cs` (modified — add a timeout to the Kernel's `HttpClient`)
```csharp
var openAiHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

builder.Services.AddKernel()
    .AddOpenAIChatCompletion(modelId: "gpt-4o", apiKey: builder.Configuration["OpenAI:ApiKey"]!, httpClient: openAiHttpClient);
```
This replaces Feature 5's `AddOpenAIChatCompletion` call (same line, `httpClient:` parameter added). 10s is a hard ceiling with buffer above the ~5s Gate 1 target — Feature 10's reliability pass is where a retry-once-on-timeout gets added if real-world latency proves inconsistent; don't add that here pre-emptively.

### `ZenLead.Api/Controllers/V1/AiController.cs` (new)
```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.UseCases.Ai;

namespace ZenLead.Api.Controllers.V1;

public record ComposeEmailApiRequest(Guid LeadId, string? Context);
public record ComposeEmailApiResponse(string Subject, string Body, int TokensUsed);

[ApiController]
[Authorize]
[Route("api/v1/ai")]
public class AiController(ComposeEmailUseCase composeEmail) : ControllerBase
{
    [HttpPost("compose-email")]
    public async Task<ActionResult<ComposeEmailApiResponse>> ComposeEmail(ComposeEmailApiRequest request, CancellationToken ct)
    {
        var workspaceId = Guid.Parse(User.FindFirstValue("workspace_id")!);

        try
        {
            var result = await composeEmail.ExecuteAsync(new ComposeEmailRequest(request.LeadId, request.Context), workspaceId, ct);
            return result is null
                ? NotFound()
                : Ok(new ComposeEmailApiResponse(result.Subject, result.Body, result.TokensUsed));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // the OpenAI HttpClient's own 10s timeout fired, not the caller cancelling the request
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "AI provider timed out. Please try again." });
        }
    }
}
```

### `ZenLead.Api/ZenLead.Api.http` (modified — append)
```http
### Compose an email draft for a lead (replace {{accessToken}} and {{leadId}} with real values from a prior register/login and lead-create call)
POST {{ZenLead.Api_HostAddress}}/api/v1/ai/compose-email
Authorization: Bearer {{accessToken}}
Content-Type: application/json

{
  "leadId": "{{leadId}}",
  "context": "Met at a SaaS conference last week"
}

###
```

## Verification
- `dotnet build ZenLead.slnx` succeeds.
- Via `ZenLead.Api.http`: register → login → create a lead → compose-email with that lead's id returns `{ subject, body, tokensUsed }` with `tokensUsed > 0`.
- Compose-email with a `leadId` belonging to a different workspace returns `404`, not the other workspace's draft.
- Temporarily point `OpenAI:ApiKey` at an invalid value (or throttle the network) to confirm the 10s timeout actually returns a `504` with the friendly message instead of hanging the request indefinitely — then restore the real key.
