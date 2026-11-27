using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support.Builders;

public sealed class CompanyBuilder(Guid workspaceId)
{
    private readonly Company _c = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Acme", CreatedAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)
    };

    public CompanyBuilder Named(string name) { _c.Name = name; return this; }
    public CompanyBuilder WithDomain(string? domain) { _c.Domain = domain; return this; }
    public CompanyBuilder InIndustry(string industry) { _c.Industry = industry; return this; }
    public CompanyBuilder InCountry(string country) { _c.Country = country; return this; }
    public CompanyBuilder WithSize(string size) { _c.Size = size; return this; }

    public Company Build() => _c;

    public async Task<Company> SaveAsync(ZenLeadDbContext db)
    {
        db.Companies.Add(_c);
        await db.SaveChangesAsync();
        return _c;
    }
}

public sealed class LeadBuilder(Guid workspaceId)
{
    private readonly Lead _l = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane Doe", Email = $"lead-{Guid.NewGuid():N}@example.test",
        Status = LeadStatus.New, Source = LeadSource.Manual, CreatedAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)
    };

    public LeadBuilder Named(string name) { _l.Name = name; return this; }
    public LeadBuilder WithEmail(string email) { _l.Email = email.ToLowerInvariant(); return this; }   // leads are stored lowercase
    public LeadBuilder WithTitle(string title) { _l.Title = title; return this; }
    public LeadBuilder WithStatus(LeadStatus status) { _l.Status = status; return this; }
    public LeadBuilder FromSource(LeadSource source, Guid? runId = null) { _l.Source = source; _l.SourceRunId = runId; return this; }
    public LeadBuilder AtCompany(Company company) { _l.CompanyId = company.Id; return this; }
    public LeadBuilder CreatedAt(DateTime utc) { _l.CreatedAt = utc; return this; }
    public LeadBuilder Deleted(DateTime utc) { _l.DeletedAt = utc; return this; }

    public Lead Build() => _l;

    public async Task<Lead> SaveAsync(ZenLeadDbContext db)
    {
        db.Leads.Add(_l);
        await db.SaveChangesAsync();
        return _l;
    }
}
