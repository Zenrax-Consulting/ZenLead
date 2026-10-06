namespace ZenLead.Application.Dtos.Auth;

public record RegisterRequest(string WorkspaceName, string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record AuthResponse(string AccessToken, string RefreshToken);
