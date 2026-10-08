using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Leads;

public static class LeadStatusRules
{
    /// <summary>Statuses we must never email again. Ingestion, enrollment (F22) and the sender (F23) all use this.</summary>
    public static bool IsSuppressed(LeadStatus status) => status is LeadStatus.Unsubscribed or LeadStatus.Bounced;

    /// <summary>
    /// Manual/API transitions. System transitions (campaign send → Contacted, inbound reply → Replied,
    /// unsubscribe → Unsubscribed, bounce → Bounced) call <see cref="CanTransition"/> too, so they obey the same table.
    /// </summary>
    public static bool CanTransition(LeadStatus from, LeadStatus to)
    {
        if (from == to) return true;
        if (IsSuppressed(from)) return false;          // Unsubscribed/Bounced are terminal; a human re-consent flow is out of scope
        return to switch
        {
            LeadStatus.New => false,                    // never go back to New
            LeadStatus.Contacted => from == LeadStatus.New,
            LeadStatus.Replied => true,                 // a reply can arrive in any non-suppressed state
            LeadStatus.Unsubscribed or LeadStatus.Bounced => true,
            _ => false
        };
    }
}
