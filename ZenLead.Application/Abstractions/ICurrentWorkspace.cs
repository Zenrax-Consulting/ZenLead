namespace ZenLead.Application.Abstractions;

/// <summary>Workspace of the caller. Null outside an authenticated HTTP request (Hangfire jobs, webhooks, startup) — by design.</summary>
public interface ICurrentWorkspace
{
    Guid? WorkspaceId { get; }
}
