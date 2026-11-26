namespace ZenLead.Domain.Enums;

// Stored as int — only ever append new values.
public enum EmailMessageStatus { Queued, Sent, Delivered, Bounced, Dropped, Failed }
