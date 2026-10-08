using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class TargetProfileRepository(ZenLeadDbContext db) : ITargetProfileRepository
{
    public Task<TargetProfile?> GetAsync(Guid id, CancellationToken ct = default)
        => db.TargetProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<TargetProfile>> ListAsync(CancellationToken ct = default)
        => await db.TargetProfiles.AsNoTracking().OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct);

    public async Task<TargetProfile> AddAsync(TargetProfile profile, CancellationToken ct = default)
    {
        db.TargetProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task UpdateAsync(TargetProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);                     // tracked: loaded via GetAsync in the same scope
    }

    public async Task DeleteAsync(TargetProfile profile, CancellationToken ct = default)
    {
        db.TargetProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
    }
}
