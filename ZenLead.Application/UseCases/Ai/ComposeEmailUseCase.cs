using ZenLead.Application.Abstractions;

namespace ZenLead.Application.UseCases.Ai;

public record ComposeEmailRequest(Guid LeadId, string? Context);
public record ComposeEmailResult(string Subject, string Body, int TokensUsed);

public class ComposeEmailUseCase(
    ILeadRepository leads, IEmailComposer composer, ITokenUsageTracker usage, AiPricing pricing)
{
    public async Task<ComposeEmailResult?> ExecuteAsync(ComposeEmailRequest request, Guid workspaceId, CancellationToken ct = default)
    {
        var lead = await leads.GetByIdAsync(request.LeadId, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return null; // same workspace-scoping discipline as LeadsController

        ComposedEmail composed;
        try
        {
            composed = await composer.ComposeAsync(
                new EmailComposeContext(lead.Name, lead.Email, lead.Title, request.Context), ct);
        }
        catch (AiProviderException ex) when (ex.PromptTokens + ex.CompletionTokens > 0)
        {
            // the provider billed us even though the reply was unusable, so the spend log must still show it
            await RecordUsageAsync(workspaceId, ex.PromptTokens, ex.CompletionTokens, ct);
            throw;
        }

        await RecordUsageAsync(workspaceId, composed.PromptTokens, composed.CompletionTokens, ct);

        return new ComposeEmailResult(composed.Subject, composed.Body, composed.TokensUsed);
    }

    private Task RecordUsageAsync(Guid workspaceId, int promptTokens, int completionTokens, CancellationToken ct)
        => usage.RecordAsync(new AiUsageEntry(
            workspaceId, pricing.Model, promptTokens, completionTokens,
            pricing.EstimateCostUsd(promptTokens, completionTokens)), ct);
}
