using Microsoft.EntityFrameworkCore;
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

    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<bool> UpdateTimeZoneAsync(Guid id, string timeZone, CancellationToken ct = default)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workspace is null) return false;
        workspace.TimeZone = timeZone;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
