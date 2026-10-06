namespace ZenLead.Application.Abstractions;

public interface IJwtTokenGenerator
{
    string GenerateAccessToken(Guid userId, Guid workspaceId, string email);
}
