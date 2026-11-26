namespace ZenLead.Domain.Enums;

/// <summary>Who set an <see cref="Entities.InboxMessage"/>'s classification. Stored as int — only ever append new values.</summary>
public enum ClassificationSource { None, Header, Ai, Manual }
