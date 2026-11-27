using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support.Builders;

/// <summary>
/// Test-data builders (T00). Each one has sensible defaults, <c>With…</c> methods for what a test cares about, <c>Build()</c> for an
/// unsaved entity and <c>SaveAsync(db)</c> to add and save it. Tenant rows always carry their WorkspaceId explicitly, so they can be
/// saved through a context with no current workspace (like a job or webhook would).
/// </summary>
public sealed class WorkspaceBuilder
{
    private readonly Workspace _w = new()
    {
        Id = Guid.NewGuid(), Name = $"Workspace {Guid.NewGuid():N}"[..20], TimeZone = "UTC", CreatedAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)
    };

    public WorkspaceBuilder Named(string name) { _w.Name = name; return this; }
    public WorkspaceBuilder InTimeZone(string ianaId) { _w.TimeZone = ianaId; return this; }
    public WorkspaceBuilder WithAdminEmail(string email) { _w.AdminEmail = email; return this; }
    public WorkspaceBuilder CreatedAt(DateTime utc) { _w.CreatedAt = utc; return this; }

    public Workspace Build() => _w;

    public async Task<Workspace> SaveAsync(ZenLeadDbContext db)
    {
        db.Workspaces.Add(_w);
        await db.SaveChangesAsync();
        return _w;
    }
}
