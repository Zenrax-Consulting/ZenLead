namespace ZenLead.Domain.Enums;

/// <summary>Only <see cref="Active"/> users can sign in or refresh. New users start as <see cref="PendingApproval"/> (F20).</summary>
public enum AppUserStatus { PendingApproval = 0, Active = 1, Rejected = 2, Disabled = 3 }
