using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Application.Dtos.Auth;
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

    public async Task<IReadOnlyList<WorkspaceOption>> SearchByNameAsync(string? q, int max, CancellationToken ct = default)
    {
        var query = db.Workspaces.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{EscapeLike(q.Trim())}%";
            query = query.Where(w => EF.Functions.Like(w.Name, like, "\\"));
        }
        // project id + name only: the anonymous picker must never see AdminEmail
        return await query.OrderBy(w => w.Name).Take(max).Select(w => new WorkspaceOption(w.Id, w.Name)).ToListAsync(ct);
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
