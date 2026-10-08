using System.Security.Claims;
using ZenLead.Application.Abstractions;

namespace ZenLead.Api;

public class HttpCurrentWorkspace(IHttpContextAccessor accessor) : ICurrentWorkspace
{
    public Guid? WorkspaceId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("workspace_id"), out var id) && id != Guid.Empty ? id : null;
}
