using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public static class TenantQueryExtensions
{
    /// <summary>
    /// For code with no HTTP context (Hangfire jobs, webhooks): drop the ambient tenant filter and scope explicitly.
    /// Soft-delete and any other filters stay on. Never call this with a workspaceId taken from request input.
    /// </summary>
    public static IQueryable<T> ForWorkspace<T>(this IQueryable<T> query, Guid workspaceId) where T : class, ITenantEntity
        => query.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]).Where(e => e.WorkspaceId == workspaceId);
}
