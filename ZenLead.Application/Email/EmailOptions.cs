namespace ZenLead.Application.Email;

/// <summary>Bound from the "Email" configuration section.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";
    public const string LogProvider = "Log";
    public const string SendGridProvider = "SendGrid";
    public const string PickupProvider = "Pickup";

    public string Provider { get; set; } = LogProvider;              // "Log" | "SendGrid" | "Pickup" (Development/E2E: JSON files)
    public string? PickupDirectory { get; set; }                     // Pickup only; relative paths resolve against the content root
    public string FromAddress { get; set; } = "business@zenraxconsulting.com";
    public string FromName { get; set; } = "Zenrax";
    public bool DomainVerified { get; set; }
    public string InboundDomain { get; set; } = "reply.leads.zenraxconsulting.com";
}
