using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Tests.Application.Leads;

public class FakeLeadIngestionStore : ILeadIngestionStore
{
    public List<Lead> Leads { get; } = [];
    public Dictionary<(Guid Workspace, string Key), Company> Companies { get; } = [];
    public HashSet<Guid> SoftDeleted { get; } = [];
    public HashSet<string> RaceLosers { get; } = [];   // emails that "a concurrent import" inserted first
    public Action<Lead>? OnInserted { get; set; }       // lets controller tests mirror inserts into a fake ILeadRepository

    public Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, ExistingLead>>(
            Leads.Where(l => l.WorkspaceId == workspaceId && emails.Contains(l.Email))
                 .ToDictionary(l => l.Email, l => new ExistingLead(l.Id, l.Status, SoftDeleted.Contains(l.Id))));

    public Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct)
    {
        var result = new Dictionary<CompanyKey, Guid>();
        foreach (var (key, industry, country, size) in companies)
        {
            var id = (workspaceId, key.Domain ?? "name:" + key.Name.ToLowerInvariant());
            if (!Companies.TryGetValue(id, out var company))
            {
                company = new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = key.Name, Domain = key.Domain,
                                        Industry = industry, Country = country, Size = size, CreatedAt = DateTime.UtcNow };
                Companies[id] = company;
            }
            else
            {
                company.Industry ??= industry; company.Country ??= country; company.Size ??= size;
            }
            result[key] = company.Id;
        }
        return Task.FromResult<IReadOnlyDictionary<CompanyKey, Guid>>(result);
    }

    public Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct)
    {
        var landed = new HashSet<Guid>();
        foreach (var lead in leads)
        {
            if (RaceLosers.Contains(lead.Email) || Leads.Any(l => l.WorkspaceId == lead.WorkspaceId && l.Email == lead.Email)) continue;
            Leads.Add(lead);
            OnInserted?.Invoke(lead);
            landed.Add(lead.Id);
        }
        return Task.FromResult<IReadOnlySet<Guid>>(landed);
    }
}
