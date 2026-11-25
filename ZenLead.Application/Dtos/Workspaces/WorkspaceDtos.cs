namespace ZenLead.Application.Dtos.Workspaces;

public record CurrentWorkspaceResponse(Guid Id, string Name);

public record UpdateTimeZoneRequest(string? TimeZone);
