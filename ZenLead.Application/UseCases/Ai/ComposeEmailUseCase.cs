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

        var composed = await composer.ComposeAsync(
            new EmailComposeContext(lead.Name, lead.Email, lead.Title, request.Context), ct);

        await usage.RecordAsync(new AiUsageEntry(
            workspaceId, pricing.Model, composed.PromptTokens, composed.CompletionTokens,
            pricing.EstimateCostUsd(composed.PromptTokens, composed.CompletionTokens)), ct);

        return new ComposeEmailResult(composed.Subject, composed.Body, composed.TokensUsed);
    }
}
