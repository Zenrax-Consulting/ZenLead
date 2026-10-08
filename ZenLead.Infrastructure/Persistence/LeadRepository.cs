using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class LeadRepository(ZenLeadDbContext db) : ILeadRepository
{
    public async Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default)
    {
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        return lead;
    }

    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Leads.Include(l => l.Company).FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await db.Leads.Include(l => l.Company).Where(l => l.WorkspaceId == workspaceId).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);

    public async Task UpdateAsync(Lead lead, CancellationToken ct = default)
    {
        lead.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);                     // lead is tracked (loaded via GetByIdAsync in the same scope)
    }

    public async Task SoftDeleteAsync(Lead lead, CancellationToken ct = default)
    {
        lead.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
