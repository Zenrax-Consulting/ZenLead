namespace ZenLead.Application.Dtos.Admin;

public record CreateWorkspaceRequest(string Name, string AdminEmail, string? TimeZone = null);

public record WorkspaceAdminResponse(Guid Id, string Name, string? AdminEmail, string TimeZone, int UserCount, DateTime CreatedAt);
