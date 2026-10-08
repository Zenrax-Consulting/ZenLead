using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class LeadRepositorySearchTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _wsA = Guid.NewGuid();
    private readonly Guid _wsB = Guid.NewGuid();
    private readonly DateTime _t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public LeadRepositorySearchTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.AddRange(
            new Workspace { Id = _wsA, Name = "A", CreatedAt = _t0 },
            new Workspace { Id = _wsB, Name = "B", CreatedAt = _t0 });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Lead Seed(Guid ws, string name, string email, int ageMinutes = 0, LeadStatus status = LeadStatus.New,
        LeadSource source = LeadSource.Manual, Guid? runId = null, Company? company = null)
    {
        var lead = new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, Name = name, Email = email, Status = status, Source = source,
            SourceRunId = runId, CreatedAt = _t0.AddMinutes(ageMinutes), CompanyId = company?.Id
        };
        using var ctx = _db.CreateContext(null);
        if (company is not null && !ctx.Companies.Any(c => c.Id == company.Id)) ctx.Companies.Add(company);
        ctx.Leads.Add(lead);
        ctx.SaveChanges();
        return lead;
    }

    private Company NewCompany(Guid ws, string name) => new() { Id = Guid.NewGuid(), WorkspaceId = ws, Name = name, CreatedAt = _t0 };

    private Task<PagedResult<Lead>> Search(Guid ws, LeadQuery q)
    {
        var ctx = _db.CreateContext(ws);
        return new LeadRepository(ctx).SearchAsync(ws, q);
    }

    [Fact]
    public async Task Paging_ReportsTotal_AndPagesDoNotOverlap()
    {
        for (var i = 0; i < 7; i++) Seed(_wsA, $"L{i}", $"l{i}@x.com", ageMinutes: i);

        var p1 = await Search(_wsA, new LeadQuery(Page: 1, PageSize: 3));
        var p2 = await Search(_wsA, new LeadQuery(Page: 2, PageSize: 3));
        var p3 = await Search(_wsA, new LeadQuery(Page: 3, PageSize: 3));

        Assert.Equal(7, p1.Total);
        Assert.Equal([3, 3, 1], new[] { p1.Items.Count, p2.Items.Count, p3.Items.Count });
        Assert.Equal(7, p1.Items.Concat(p2.Items).Concat(p3.Items).Select(l => l.Id).Distinct().Count());
        Assert.Equal("L6", p1.Items[0].Name); // default: newest first
    }

    [Fact]
    public async Task Sorting_WithTies_IsDeterministicAcrossPages()
    {
        for (var i = 0; i < 6; i++) Seed(_wsA, "Same", $"s{i}@x.com"); // identical name and createdAt

        var a = (await Search(_wsA, new LeadQuery(Page: 1, PageSize: 3, Sort: "name"))).Items.Select(l => l.Id);
        var b = (await Search(_wsA, new LeadQuery(Page: 2, PageSize: 3, Sort: "name"))).Items.Select(l => l.Id);

        Assert.Equal(6, a.Concat(b).Distinct().Count());
    }

    [Fact]
    public async Task Sort_ByNameAscending_AndDescending()
    {
        Seed(_wsA, "Bob", "b@x.com"); Seed(_wsA, "Al", "a@x.com"); Seed(_wsA, "Cy", "c@x.com");

        Assert.Equal(["Al", "Bob", "Cy"], (await Search(_wsA, new LeadQuery(Sort: "name"))).Items.Select(l => l.Name));
        Assert.Equal(["Al", "Bob", "Cy"], (await Search(_wsA, new LeadQuery(Sort: "-name"))).Items.Select(l => l.Name).Reverse());
    }

    [Fact]
    public async Task Filters_ByStatus_Source_Company_AndRun()
    {
        var acme = NewCompany(_wsA, "Acme");
        var run = Guid.NewGuid();
        var target = Seed(_wsA, "T", "t@x.com", status: LeadStatus.Replied, source: LeadSource.Discovery, runId: run, company: acme);
        Seed(_wsA, "Other", "o@x.com");

        Assert.Equal(target.Id, Assert.Single((await Search(_wsA, new LeadQuery(Status: LeadStatus.Replied))).Items).Id);
        Assert.Equal(target.Id, Assert.Single((await Search(_wsA, new LeadQuery(Source: LeadSource.Discovery))).Items).Id);
        Assert.Equal(target.Id, Assert.Single((await Search(_wsA, new LeadQuery(CompanyId: acme.Id))).Items).Id);
        Assert.Equal(target.Id, Assert.Single((await Search(_wsA, new LeadQuery(SourceRunId: run))).Items).Id);
    }

    [Fact]
    public async Task Q_MatchesNameEmailAndCompanyName()
    {
        var acme = NewCompany(_wsA, "Acme Corp");
        var byCompany = Seed(_wsA, "Zed", "zed@x.com", company: acme);
        var byName = Seed(_wsA, "Acme Fan", "fan@x.com");
        var byEmail = Seed(_wsA, "Qux", "qux@acme.io");
        Seed(_wsA, "Nope", "nope@x.com");

        var ids = (await Search(_wsA, new LeadQuery(Q: "acme"))).Items.Select(l => l.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { byCompany.Id, byName.Id, byEmail.Id }, ids);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    public async Task Q_WildcardCharacters_AreLiteral(string q)
    {
        var literal = Seed(_wsA, $"100{q} Club", "club@x.com");
        Seed(_wsA, "Plain", "plain@x.com");

        var result = await Search(_wsA, new LeadQuery(Q: q));

        Assert.Equal(literal.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Search_NeverReturnsAnotherWorkspacesRows()
    {
        var mine = Seed(_wsA, "Mine", "mine@x.com");
        Seed(_wsB, "Theirs", "theirs@x.com");

        var result = await Search(_wsA, new LeadQuery());

        Assert.Equal(mine.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Total);

        using var ctx = _db.CreateContext(_wsA);
        var ids = await new LeadRepository(ctx).ListIdsAsync(_wsA, new LeadQuery(), 100);
        Assert.Equal([mine.Id], ids);
    }

    [Fact]
    public async Task ListIds_HonoursMax_AndFilter()
    {
        for (var i = 0; i < 5; i++) Seed(_wsA, $"L{i}", $"l{i}@x.com");
        Seed(_wsA, "Replied", "r@x.com", status: LeadStatus.Replied);

        using var ctx = _db.CreateContext(_wsA);
        var repo = new LeadRepository(ctx);

        Assert.Equal(3, (await repo.ListIdsAsync(_wsA, new LeadQuery(), 3)).Count);
        Assert.Single(await repo.ListIdsAsync(_wsA, new LeadQuery(Status: LeadStatus.Replied), 100));
    }
}
