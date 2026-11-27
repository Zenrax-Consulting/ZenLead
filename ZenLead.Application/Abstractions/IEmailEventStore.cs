using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

/// <summary>Everything an event's consequences may touch, loaded (tracked) in a bounded number of queries.</summary>
public sealed class EventEffectTargets
{
    public Dictionary<Guid, CampaignEnrollment> Enrollments { get; } = [];
    public Dictionary<Guid, Lead> Leads { get; } = [];

    /// <summary>Active/Paused enrollments of each lead, across campaigns (spam reports and unsubscribes stop them all).</summary>
    public Dictionary<Guid, List<CampaignEnrollment>> LiveEnrollmentsByLead { get; } = [];

    /// <summary><see cref="SuppressionKey"/>s of addresses already suppressed, or staged earlier in this batch.</summary>
    public HashSet<string> Suppressed { get; } = new(StringComparer.Ordinal);

    public static string SuppressionKey(Guid workspaceId, string email) => $"{workspaceId:N}|{email.Trim().ToLowerInvariant()}";
}

/// <summary>
/// Persistence for the Event Webhook (F28). Anonymous like F25: lookups are by (workspaceId, id) pairs from our own custom args;
/// everything loaded is tracked and nothing is saved until <see cref="CommitAsync"/>, which writes all state changes AND the
/// processed event ids in ONE transaction, so a failed batch leaves no trace and SendGrid's retry applies cleanly.
/// </summary>
public interface IEmailEventStore
{
    /// <summary>The subset of <paramref name="keys"/> not processed yet.</summary>
    Task<IReadOnlySet<string>> FilterUnprocessedAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

    /// <summary>Tracked messages for the given (workspace, message) pairs, by id; a pair whose workspace doesn't match is absent.</summary>
    Task<IReadOnlyDictionary<Guid, EmailMessage>> LoadMessagesAsync(IReadOnlyCollection<(Guid WorkspaceId, Guid MessageId)> keys, CancellationToken ct);

    /// <summary>Tracked enrollments, leads (soft-deleted included), the leads' live enrollments and existing suppressions for these messages.</summary>
    Task<EventEffectTargets> LoadEffectTargetsAsync(IReadOnlyCollection<EmailMessage> messages, CancellationToken ct);

    /// <summary>Staged only; written by <see cref="CommitAsync"/>.</summary>
    void StageSuppression(SuppressedEmail row);

    /// <summary>
    /// Saves every tracked change plus one <see cref="ProcessedWebhookEvent"/> per key, atomically. Returns false (nothing written)
    /// when a concurrent delivery of the same events committed first.
    /// </summary>
    Task<bool> CommitAsync(IReadOnlyCollection<string> processedKeys, CancellationToken ct);
}
