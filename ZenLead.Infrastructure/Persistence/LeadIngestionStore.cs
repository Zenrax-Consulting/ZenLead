using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class LeadIngestionStore(ZenLeadDbContext db) : ILeadIngestionStore
{
    public async Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(
        Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        var rows = await db.Leads
            .IgnoreQueryFilters()                                        // include soft-deleted AND work with no HTTP context
            .Where(l => l.WorkspaceId == workspaceId && emails.Contains(l.Email))
            .Select(l => new { l.Email, l.Id, l.Status, Deleted = l.DeletedAt != null })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Email, r => new ExistingLead(r.Id, r.Status, r.Deleted));
    }

    public async Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct)
    {
        var domains = companies.Select(c => c.Key.Domain).Where(d => d != null).Cast<string>().ToList();
        var names = companies.Where(c => c.Key.Domain is null).Select(c => c.Key.Name.ToLower()).ToList();

        var existing = await db.Companies.ForWorkspace(workspaceId)
            .Where(c => (c.Domain != null && domains.Contains(c.Domain)) || (c.Domain == null && names.Contains(c.Name.ToLower())))
            .ToListAsync(ct);

        var result = new Dictionary<CompanyKey, Guid>();
        foreach (var (key, industry, country, size) in companies)
        {
            var match = key.Domain is not null
                ? existing.FirstOrDefault(c => c.Domain == key.Domain)
                : existing.FirstOrDefault(c => c.Domain == null && string.Equals(c.Name, key.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = key.Name, Domain = key.Domain,
                                      Industry = industry, Country = country, Size = size, CreatedAt = DateTime.UtcNow };
                db.Companies.Add(match);
                existing.Add(match);
            }
            else // fill blanks only; never overwrite what a human typed
            {
                match.Industry ??= industry; match.Country ??= country; match.Size ??= size;
            }
            result[key] = match.Id;
        }
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct)
    {
        db.Leads.AddRange(leads);
        try
        {
            await db.SaveChangesAsync(ct);
            return leads.Select(l => l.Id).ToHashSet();
        }
        catch (DbUpdateException)
        {
            // most likely the unique (WorkspaceId, Email) index: a concurrent import inserted one of ours. Retry one by one.
            db.ChangeTracker.Clear();
            var landed = new HashSet<Guid>();
            foreach (var lead in leads)
            {
                db.Leads.Add(lead);
                try { await db.SaveChangesAsync(ct); landed.Add(lead.Id); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            }
            return landed;
        }
    }
}
