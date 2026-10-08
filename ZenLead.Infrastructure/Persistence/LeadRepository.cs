using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
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

    public async Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery q, CancellationToken ct = default)
    {
        var query = ApplyFilters(db.Leads.AsNoTracking().Include(l => l.Company).Where(l => l.WorkspaceId == workspaceId), q);
        var total = await query.CountAsync(ct);
        var items = await ApplySort(query, q.Sort).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return new PagedResult<Lead>(items, total, q.Page, q.PageSize);
    }

    public async Task<IReadOnlyList<Lead>> GetByIdsAsync(Guid workspaceId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => await db.Leads.AsNoTracking().Include(l => l.Company)
            .Where(l => l.WorkspaceId == workspaceId && ids.Contains(l.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(Guid workspaceId, LeadQuery q, int max, CancellationToken ct = default)
        => await ApplySort(ApplyFilters(db.Leads.AsNoTracking().Where(l => l.WorkspaceId == workspaceId), q), q.Sort)
            .Select(l => l.Id).Take(max).ToListAsync(ct);

    private static IQueryable<Lead> ApplyFilters(IQueryable<Lead> query, LeadQuery q)
    {
        if (q.Status is { } status) query = query.Where(l => l.Status == status);
        if (q.CompanyId is { } companyId) query = query.Where(l => l.CompanyId == companyId);
        if (q.Source is { } source) query = query.Where(l => l.Source == source);
        if (q.SourceRunId is { } runId) query = query.Where(l => l.SourceRunId == runId);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{EscapeLike(q.Q.Trim())}%";
            query = query.Where(l => EF.Functions.Like(l.Name, like, "\\") || EF.Functions.Like(l.Email, like, "\\")
                                  || (l.Company != null && EF.Functions.Like(l.Company.Name, like, "\\")));
        }
        return query;
    }

    private static IQueryable<Lead> ApplySort(IQueryable<Lead> query, string? sort)
    {
        var desc = sort?.StartsWith('-') ?? true;               // default newest first
        return sort?.TrimStart('-') switch
        {
            "name" => desc ? query.OrderByDescending(l => l.Name).ThenBy(l => l.Id) : query.OrderBy(l => l.Name).ThenBy(l => l.Id),
            "email" => desc ? query.OrderByDescending(l => l.Email).ThenBy(l => l.Id) : query.OrderBy(l => l.Email).ThenBy(l => l.Id),
            "status" => desc ? query.OrderByDescending(l => l.Status).ThenBy(l => l.Id) : query.OrderBy(l => l.Status).ThenBy(l => l.Id),
            "company" => desc ? query.OrderByDescending(l => l.Company!.Name).ThenBy(l => l.Id) : query.OrderBy(l => l.Company!.Name).ThenBy(l => l.Id),
            _ => desc ? query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id) : query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
        };
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

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
