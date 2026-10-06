using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class WorkspaceRepository(ZenLeadDbContext db) : IWorkspaceRepository
{
    public async Task<Workspace> CreateAsync(string name, CancellationToken ct = default)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        return workspace;
    }
}
