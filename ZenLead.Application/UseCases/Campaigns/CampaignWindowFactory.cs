using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.UseCases.Campaigns;

public static class CampaignWindowFactory
{
    /// <summary>
    /// The campaign's send window in its zone: <see cref="Workspace.TimeZone"/>, overridden by <see cref="Campaign.TimeZoneId"/>.
    /// Falls back to UTC (with a warning) if a stored id no longer resolves on this machine.
    /// </summary>
    public static SendWindow For(Campaign c, string workspaceTimeZone, ILogger? logger = null)
    {
        var id = c.TimeZoneId ?? workspaceTimeZone;
        if (!TimeZones.TryFind(id, out var zone))             // zone = UTC when not found
            logger?.LogWarning("Campaign {CampaignId} time zone '{TimeZone}' does not resolve; scheduling in UTC.", c.Id, id);
        return new SendWindow(zone, c.WindowStart, c.WindowEnd, c.SendDays);
    }

    /// <summary>The zone id scheduling actually uses ("UTC" when the configured one doesn't resolve).</summary>
    public static string EffectiveTimeZone(Campaign c, string workspaceTimeZone)
    {
        var id = c.TimeZoneId ?? workspaceTimeZone;
        return TimeZones.TryFind(id, out _) ? id : "UTC";
    }

    public static async Task<string> WorkspaceTimeZoneAsync(this IWorkspaceRepository workspaces, Guid workspaceId, CancellationToken ct)
        => (await workspaces.GetByIdAsync(workspaceId, ct))?.TimeZone ?? "UTC";
}
