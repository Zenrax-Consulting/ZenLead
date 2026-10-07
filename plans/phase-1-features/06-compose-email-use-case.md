# Feature 6 — Compose-Email Use Case

**Branch:** `feature/compose-email-use-case`
**Milestone:** 2 — AI loop
**Depends on:** Feature 5 (`IEmailComposer`/`Kernel` registered).

## Goal
Replace Feature 5's placeholder prompt with the real one (structured JSON subject/body, guardrails against fabricated claims), add the `ComposeEmailUseCase` that loads a real `Lead` and calls it, and log token usage on every call.

## Files to modify/add

### `ZenLead.Infrastructure/Ai/EmailComposer.cs` (modified — replaces Feature 5's placeholder body)
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Ai;

internal record SubjectBodyDto(string Subject, string Body);

public class EmailComposer(Kernel kernel, ILogger<EmailComposer> logger) : IEmailComposer
{
    private const string SystemPrompt = """
        You write short, personalised cold-outreach emails on behalf of a sender reaching out to a sales lead.
        Rules:
        - Keep it under 120 words.
        - Reference the lead's name and title naturally; do not fabricate facts about the lead's company.
        - Never invent claims about the sender's own company — use only what's given in the context, or stay generic.
        - Tone: professional, warm, not salesy.
        Respond with JSON matching: { "subject": string, "body": string }.
        """;

    public async Task<ComposedEmail> ComposeAsync(EmailComposeContext context, CancellationToken ct = default)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(BuildUserMessage(context));

        var settings = new OpenAIPromptExecutionSettings { ResponseFormat = typeof(SubjectBodyDto) };
        var response = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);

        var tokensUsed = ExtractTokenUsage(response);
        logger.LogInformation("compose-email call used {TokensUsed} tokens for lead {LeadEmail}", tokensUsed, context.LeadEmail);

        var parsed = JsonSerializer.Deserialize<SubjectBodyDto>(response.Content!, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Model did not return the expected JSON shape.");

        return new ComposedEmail(parsed.Subject, parsed.Body, tokensUsed);
    }

    private static string BuildUserMessage(EmailComposeContext context) =>
        $"""
        Lead name: {context.LeadName}
        Lead email: {context.LeadEmail}
        Lead title: {context.LeadTitle ?? "unknown"}
        Additional context: {context.AdditionalContext ?? "none"}
        """;

    private static int ExtractTokenUsage(ChatMessageContent response)
    {
        if (response.Metadata is not null && response.Metadata.TryGetValue("Usage", out var usage) && usage is not null)
        {
            var property = usage.GetType().GetProperty("TotalTokens") ?? usage.GetType().GetProperty("TotalTokenCount");
            if (property?.GetValue(usage) is int total) return total;
        }
        return 0;
    }
}
```
**Note (deserialization):** the model returns camelCase JSON (`"subject"`, `"body"`) while `SubjectBodyDto` is PascalCase, and `System.Text.Json` is case-sensitive by default — hence `JsonSerializerDefaults.Web` above. Without it real responses deserialize to null fields.

**Note:** the exact `Usage` metadata key/property name depends on the `Microsoft.SemanticKernel.Connectors.OpenAI` version resolved in Feature 5 — confirm the real property name against the installed version and adjust `ExtractTokenUsage` if needed. It's written defensively (reflection + fallback to `0`) so a connector version bump degrades token counting rather than throwing — this is the "basic guardrail" the parent plan calls for; the full `AiGenerationLog` table is a Phase 2 item.

### `ZenLead.Application/UseCases/Ai/ComposeEmailUseCase.cs` (new)
```csharp
using ZenLead.Application.Abstractions;

namespace ZenLead.Application.UseCases.Ai;

public record ComposeEmailRequest(Guid LeadId, string? Context);
public record ComposeEmailResult(string Subject, string Body, int TokensUsed);

public class ComposeEmailUseCase(ILeadRepository leads, IEmailComposer composer)
{
    public async Task<ComposeEmailResult?> ExecuteAsync(ComposeEmailRequest request, Guid workspaceId, CancellationToken ct = default)
    {
        var lead = await leads.GetByIdAsync(request.LeadId, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return null; // same workspace-scoping discipline as LeadsController

        var composed = await composer.ComposeAsync(
            new EmailComposeContext(lead.Name, lead.Email, lead.Title, request.Context), ct);

        return new ComposeEmailResult(composed.Subject, composed.Body, composed.TokensUsed);
    }
}
```
No `Company` entity lookup — per the parent plan, a free-text `Context` string on the request stands in for it this phase.

## Not in this feature
No controller (Feature 7), no Angular UI (Feature 8), no fake-composer unit tests (Feature 9 — this feature only touches the real `EmailComposer`/`ComposeEmailUseCase` wiring).

## Verification
- `dotnet build ZenLead.slnx` succeeds.
- Reuse the scratch call from Feature 5 (or a quick throwaway test) to confirm `ComposeEmailUseCase.ExecuteAsync` against a real seeded `Lead` returns a subject/body that reads as personalised, and that `tokensUsed` is non-zero (confirms `ExtractTokenUsage` found the right property).
