using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

public interface IJwtTokenGenerator
{
    /// <summary>No <c>workspace_id</c> claim when <paramref name="workspaceId"/> is null; a <c>role</c> claim only for a platform role.</summary>
    string GenerateAccessToken(Guid userId, Guid? workspaceId, string email, PlatformRole role = PlatformRole.None);
}
