namespace ZenLead.Domain.Enums;

/// <summary>
/// Stored as int — only ever append new values. F25 only assigns <see cref="OutOfOffice"/>/<see cref="Bounce"/> from headers;
/// the AI classifier (F26) assigns the rest.
/// </summary>
public enum ReplyClassification { Unclassified, Interested, NotInterested, Unsubscribe, OutOfOffice, Bounce }
