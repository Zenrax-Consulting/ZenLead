namespace ZenLead.Application.Auth;

/// <summary>Bound from the "Registration" config section (F20).</summary>
public class RegistrationOptions
{
    public const string SectionName = "Registration";

    public int ApprovalLifetimeDays { get; set; } = 7;
    public int MaxPendingPerWorkspace { get; set; } = 20;

    /// <summary>A repeated request for a still-pending account re-sends the approval email at most this often.</summary>
    public TimeSpan ResendAfter { get; set; } = TimeSpan.FromHours(1);
}
