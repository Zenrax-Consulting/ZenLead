namespace ZenLead.Application.Abstractions;

/// <summary>
/// Signed, non-expiring tokens for unsubscribe links (one per workspace + lead). The public endpoint that consumes them is F23.4.
/// </summary>
public interface IUnsubscribeTokens
{
    string Create(Guid workspaceId, Guid leadId);

    /// <summary>Constant-time MAC check; false (never an exception) on any tampering or format error.</summary>
    bool TryParse(string token, out Guid workspaceId, out Guid leadId);
}
