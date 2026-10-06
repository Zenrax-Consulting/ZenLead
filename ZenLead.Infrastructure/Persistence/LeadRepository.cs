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
        => db.Leads.FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await db.Leads.Where(l => l.WorkspaceId == workspaceId).ToListAsync(ct);
}
