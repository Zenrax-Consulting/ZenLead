namespace ZenLead.Domain.Enums;

// Stored as int — only ever append new values. The inbound dedupe index filters on Direction = 0 (Inbound).
public enum MessageDirection { Inbound, Outbound }
