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
