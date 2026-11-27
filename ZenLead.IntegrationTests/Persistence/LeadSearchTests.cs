using ZenLead.Application.Dtos.Leads;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>
/// Lead list search and sorting through the API on SQL Server (F13). <c>LIKE</c> wildcards in <c>q</c> must match literally
/// (parking-lot F13 <c>q=%</c>), and every sort key must page stably when values tie (secondary key on Id).
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class LeadSearchTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    private Guid _workspaceId;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync();
        _workspaceId = workspaceId;
        _client = factory.ClientFor(token);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("%", "100% Growth")]
    [InlineData("_", "snake_case Ltd")]
    [InlineData("[B", "Bracket [Beta]")]
    [InlineData("[a-z]", "Range [a-z] Inc")]
    [InlineData("\\", "Back\\slash")]
    public async Task Wildcards_InQ_MatchLiterally(string q, string onlyMatch)
    {
        await SeedNamesAsync("100% Growth", "snake_case Ltd", "Bracket [Beta]", "Range [a-z] Inc", "Back\\slash", "Plain Name", "Another Plain");

        var page = await SearchAsync($"q={Uri.EscapeDataString(q)}");

        Assert.Equal([onlyMatch], page.Items.Select(l => l.Name));
    }

    [Fact]
    public async Task Q_MatchesNameEmailAndCompany_CaseInsensitively()
    {
        await using (var db = factory.Sql.CreateContext())
        {
            var acme = await new CompanyBuilder(_workspaceId).Named("ACME Holdings").SaveAsync(db);
            await new LeadBuilder(_workspaceId).Named("By Company").AtCompany(acme).SaveAsync(db);
            await new LeadBuilder(_workspaceId).Named("By Email").WithEmail("someone@acme.example.test").SaveAsync(db);
            await new LeadBuilder(_workspaceId).Named("Acme In Name").SaveAsync(db);
            await new LeadBuilder(_workspaceId).Named("Unrelated").SaveAsync(db);
        }

        var page = await SearchAsync("q=acme");

        Assert.Equal(["Acme In Name", "By Company", "By Email"], page.Items.Select(l => l.Name).Order());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("-name")]
    [InlineData("email")]
    [InlineData("status")]
    [InlineData("-status")]
    [InlineData("company")]
    [InlineData("createdAt")]
    [InlineData("-createdAt")]
    public async Task EverySortKey_PagesStably_WhenValuesTie(string sort)
    {
        // 9 leads, all with the same name, status, company and created time: only the Id tiebreaker can order them
        var created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = factory.Sql.CreateContext())
        {
            var company = await new CompanyBuilder(_workspaceId).Named("Same Co").SaveAsync(db);
            for (var i = 0; i < 9; i++)
                await new LeadBuilder(_workspaceId).Named("Same Name").WithEmail($"same-{i}@example.test").AtCompany(company).CreatedAt(created).SaveAsync(db);
        }

        var seen = new List<Guid>();
        for (var pageNo = 1; pageNo <= 5; pageNo++)
            seen.AddRange((await SearchAsync($"sort={Uri.EscapeDataString(sort)}&page={pageNo}&pageSize=2")).Items.Select(l => l.Id));
        var again = new List<Guid>();
        for (var pageNo = 1; pageNo <= 5; pageNo++)
            again.AddRange((await SearchAsync($"sort={Uri.EscapeDataString(sort)}&page={pageNo}&pageSize=2")).Items.Select(l => l.Id));

        Assert.Equal(9, seen.Count);
        Assert.Equal(9, seen.Distinct().Count());       // no lead on two pages, none skipped
        Assert.Equal(seen, again);                      // and the same order every time
    }

    private async Task SeedNamesAsync(params string[] names)
    {
        await using var db = factory.Sql.CreateContext();
        foreach (var name in names) await new LeadBuilder(_workspaceId).Named(name).SaveAsync(db);
    }

    private async Task<PagedResult<LeadResponse>> SearchAsync(string query)
        => await (await _client.GetAsync($"/api/v1/leads?{query}")).ReadAsync<PagedResult<LeadResponse>>();
}
