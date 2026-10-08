using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace ZenLead.Api;

public static class CurrentUser
{
    /// <summary>The caller's workspace from the JWT "workspace_id" claim, or null if it is missing or malformed.</summary>
    public static Guid? WorkspaceId(this ControllerBase controller)
        => Guid.TryParse(controller.User.FindFirstValue("workspace_id"), out var id) && id != Guid.Empty ? id : null;

    /// <summary>The caller's user id from the JWT "sub" claim, or null if it is missing or malformed.</summary>
    public static Guid? UserId(this ControllerBase controller)
        => Guid.TryParse(controller.User.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;
}
