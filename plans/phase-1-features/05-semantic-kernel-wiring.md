# Feature 5 — Semantic Kernel Wiring

**Branch:** `feature/semantic-kernel-wiring`
**Milestone:** 2 — AI loop
**Depends on:** Milestone 1 complete (an `[Authorize]`-protected API already exists; DI/Program.cs conventions are established).

## Goal
`Kernel`/`IChatCompletionService` is registered in DI, and `IEmailComposer` exists as the swap-safe abstraction Application-layer code will call — with a placeholder prompt just good enough to prove the OpenAI call round-trips. The *real* prompt and the use case that calls it are Feature 6; don't over-build the prompt here.

## PBI 5.1 — OpenAI account & spend cap (prerequisite, no code)
Before touching any code in this feature: create the OpenAI account/API key and set a **hard $20 usage cap in the OpenAI dashboard** (not just an in-app limit). Do this first — account verification can take time and shouldn't block the rest of the feature.

## Files to add

### `ZenLead.Infrastructure.csproj` (modified)
```xml
<PackageReference Include="Microsoft.SemanticKernel" Version="1.*" />
<PackageReference Include="Microsoft.SemanticKernel.Connectors.OpenAI" Version="1.*" />
```

### `ZenLead.Application/Abstractions/IEmailComposer.cs` (new)
```csharp
namespace ZenLead.Application.Abstractions;

public record EmailComposeContext(string LeadName, string LeadEmail, string? LeadTitle, string? AdditionalContext);
public record ComposedEmail(string Subject, string Body, int TokensUsed);

public interface IEmailComposer
{
    Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default);
}
```
This is the one interface call sites must depend on — nothing in `ZenLead.Application` or `ZenLead.Api` may reference `Microsoft.SemanticKernel`/OpenAI types directly, per the locked decision in [phase-1-poc-implementation-plan.md](../phase-1-poc-implementation-plan.md) §1.

### `ZenLead.Infrastructure/Ai/EmailComposer.cs` (new — placeholder prompt, replaced in Feature 6)
```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

public class EmailComposer(Kernel kernel) : IEmailComposer
{
    public async Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage("You are a cold-outreach email assistant. Write a short, personalised email.");
        history.AddUserMessage($"Lead: {context.LeadName} ({context.LeadEmail}), title: {context.LeadTitle ?? "unknown"}.");

        var response = await chat.GetChatMessageContentAsync(history, kernel: kernel, cancellationToken: ct);

        return new ComposedEmail("Quick question", response.Content ?? string.Empty, 0);
    }
}
```
This proves the wiring works end-to-end (DI → `Kernel` → OpenAI → a response comes back) but the subject is hardcoded and `TokensUsed` is always `0` — Feature 6 replaces the prompt with the real system prompt, structured JSON output, and real token accounting.

### `ZenLead.Api/Program.cs` (modified — additions only, inserted after the Milestone-1 DI registrations)
```csharp
using Microsoft.SemanticKernel;
using ZenLead.Infrastructure.Ai;

// ...

builder.Services.AddKernel()
    .AddOpenAIChatCompletion(modelId: "gpt-4o", apiKey: builder.Configuration["OpenAI:ApiKey"]!);

builder.Services.AddScoped<IEmailComposer, EmailComposer>();
```

### User secrets (command, not a file)
```
dotnet user-secrets set "OpenAI:ApiKey" "<key from the capped account created in PBI 5.1>" --project ZenLead.Api
```

## Not in this feature
No `ComposeEmailUseCase`, no real prompt, no controller, no token-usage logging — all Feature 6/7.

## Verification
- `dotnet build ZenLead.slnx` succeeds.
- A throwaway manual call (e.g. a temporary test endpoint, or a scratch console snippet resolving `IEmailComposer` from the DI container) confirms a real OpenAI response comes back — delete the scratch code before merging; Feature 7 adds the real endpoint.
