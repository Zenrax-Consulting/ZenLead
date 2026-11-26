namespace ZenLead.Application.Sending;

/// <summary>
/// <c>Restricted</c> (the default everywhere): only recipients on <see cref="SendingOptions.RecipientAllowList"/> are ever mailed,
/// and an empty list mails nobody. <c>Live</c>: real prospects can be mailed. Fail-closed by design.
/// </summary>
public enum SendingMode { Restricted, Live }

/// <summary>Bound from the "Sending" configuration section (the campaign sender, F23).</summary>
public class SendingOptions
{
    public const string SectionName = "Sending";

    public bool Enabled { get; set; } = true;
    public int WorkspaceDailyCap { get; set; } = 50;              // new-domain warm-up; raise by config as reputation builds
    public int MaxPerWorkspacePerRun { get; set; } = 20;          // = per-minute throttle at a 1-minute cadence
    public int BatchSize { get; set; } = 200;
    public int MaxAttempts { get; set; } = 3;
    public int[] BackoffMinutes { get; set; } = [5, 30, 120];
    public int StaleQueuedMinutes { get; set; } = 10;
    public SendingMode Mode { get; set; } = SendingMode.Restricted;
    public string[] RecipientAllowList { get; set; } = [];        // exact addresses or domains ("acme.com" or "@acme.com")

    /// <summary>Whether <paramref name="email"/> may be sent to under the current mode. Case-insensitive; domains match exactly.</summary>
    public bool Allows(string email)
    {
        if (Mode == SendingMode.Live) return true;
        var address = email.Trim();
        var at = address.LastIndexOf('@');
        var domain = at >= 0 ? address[(at + 1)..] : string.Empty;
        foreach (var raw in RecipientAllowList)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            if (entry.StartsWith('@')) entry = entry[1..];
            var match = entry.Contains('@')
                ? entry.Equals(address, StringComparison.OrdinalIgnoreCase)
                : entry.Equals(domain, StringComparison.OrdinalIgnoreCase);
            if (match) return true;
        }
        return false;
    }
}
