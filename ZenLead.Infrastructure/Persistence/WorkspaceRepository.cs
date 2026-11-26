using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class WorkspaceRepository(ZenLeadDbContext db) : IWorkspaceRepository
{
    public async Task<Workspace> CreateAsync(string name, string? adminEmail = null, string timeZone = "UTC", CancellationToken ct = default)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, AdminEmail = adminEmail, TimeZone = timeZone, CreatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(workspace).State = EntityState.Detached;
            // provider-agnostic: if the name is now taken, the unique index is what failed
            if (await NameExistsAsync(name, ct)) throw new WorkspaceNameTakenException();
            throw;
        }
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

    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToUpperInvariant();
        return db.Workspaces.AnyAsync(w => w.Name.Trim().ToUpper() == normalized, ct);
    }

    public async Task<IReadOnlyList<WorkspaceAdminResponse>> ListWithUserCountsAsync(CancellationToken ct = default)
    {
        var rows = await db.Workspaces
            .OrderBy(w => w.Name)
            .Select(w => new { w.Id, w.Name, w.AdminEmail, w.TimeZone, w.CreatedAt, UserCount = db.Users.Count(u => u.WorkspaceId == w.Id) })
            .ToListAsync(ct);
        return rows.Select(r => new WorkspaceAdminResponse(r.Id, r.Name, r.AdminEmail, r.TimeZone, r.UserCount, r.CreatedAt)).ToList();
    }
}
