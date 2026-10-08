using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Infrastructure.Persistence;

public class CompanyRepository(ZenLeadDbContext db) : ICompanyRepository
{
    public async Task<IReadOnlyList<CompanySummary>> SearchAsync(Guid workspaceId, string? q, int max, CancellationToken ct = default)
    {
        var query = db.Companies.AsNoTracking().Where(c => c.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[")}%";
            query = query.Where(c => EF.Functions.Like(c.Name, like, "\\") || (c.Domain != null && EF.Functions.Like(c.Domain, like, "\\")));
        }
        return await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Take(max)
            .Select(c => new CompanySummary(c.Id, c.Name, c.Domain, c.Industry, c.Country))
            .ToListAsync(ct);
    }

    public Task<CompanySummary?> GetByIdAsync(Guid workspaceId, Guid id, CancellationToken ct = default)
        => db.Companies.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Id == id)
            .Select(c => new CompanySummary(c.Id, c.Name, c.Domain, c.Industry, c.Country))
            .FirstOrDefaultAsync(ct);
}
