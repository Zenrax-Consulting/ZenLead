namespace ZenLead.Application.Dtos.Auth;

public record RegisterRequest(string Email, string Password, string DisplayName, Guid WorkspaceId);
public record RegistrationAcceptedResponse(string Message);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken);

/// <summary><see cref="Status"/> is "pending", "used" or "expired"; requester details are present only while pending.</summary>
public record ApprovalInfoResponse(string Status, string? RequesterName, string? RequesterEmail, string? WorkspaceName);

/// <summary>What the anonymous registration picker may see about a workspace: never the admin email.</summary>
public record WorkspaceOption(Guid Id, string Name);
