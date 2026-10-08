namespace ZenLead.Domain.Enums;

// Stored as int — only ever append new values.
public enum LeadStatus
{
    New,
    Contacted,
    Replied,
    Unsubscribed,
    Bounced
}
