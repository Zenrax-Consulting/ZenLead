namespace ZenLead.Domain.Enums;

/// <summary>Delivery state of a manual inbox reply (F27). Null on inbound messages and campaign sends. Stored as int — only append.</summary>
public enum OutboundDeliveryStatus { Pending, Sent, Failed }
