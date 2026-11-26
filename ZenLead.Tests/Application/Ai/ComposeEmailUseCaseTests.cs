using ZenLead.Application.Sending;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Ai;

public class ComposeEmailUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_LeadInCallersWorkspace_ReturnsComposedEmail()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var composer = new FakeEmailComposer();
        var sut = new ComposeEmailUseCase(leads, composer, new FakeTokenUsageTracker(), new AiPricing(), new FakeAiBudget());

        var result = await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, "context"), workspaceId);

        Assert.NotNull(result);
        Assert.Equal("Subject", result!.Subject);
        Assert.Equal("Jane", composer.LastContext!.LeadName);
    }

    [Fact]
    public async Task ExecuteAsync_LeadInDifferentWorkspace_ReturnsNull()
    {
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var sut = new ComposeEmailUseCase(leads, new FakeEmailComposer(), new FakeTokenUsageTracker(), new AiPricing(), new FakeAiBudget());

        var result = await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_LeadWithCompany_PassesCompanyDetailsToComposer()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow,
            Company = new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Acme", Industry = "SaaS", Country = "DE" }
        };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var composer = new FakeEmailComposer();
        var sut = new ComposeEmailUseCase(leads, composer, new FakeTokenUsageTracker(), new AiPricing(), new FakeAiBudget());

        await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), workspaceId);

        Assert.Equal("Acme", composer.LastContext!.CompanyName);
        Assert.Equal("SaaS", composer.LastContext.CompanyIndustry);
        Assert.Equal("DE", composer.LastContext.CompanyCountry);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetExceeded_ThrowsWithoutCallingTheModel()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var composer = new FakeEmailComposer();
        var usage = new FakeTokenUsageTracker();
        var sut = new ComposeEmailUseCase(leads, composer, usage, new AiPricing(),
            new FakeAiBudget { State = BudgetState.Exceeded, ResetsOn = new DateOnly(2026, 12, 1) });

        var ex = await Assert.ThrowsAsync<AiBudgetExceededException>(() => sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), workspaceId));

        Assert.Equal(new DateOnly(2026, 12, 1), ex.ResetsOn);
        Assert.Contains("1 December 2026", ex.Message);
        Assert.Null(composer.LastContext);
        Assert.Empty(usage.Entries);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsComposePurpose()
    {
        var workspaceId = Guid.NewGuid();
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow };
        var leads = new FakeLeadRepositoryForAi();
        leads.Seed(lead);
        var usage = new FakeTokenUsageTracker();
        var sut = new ComposeEmailUseCase(leads, new FakeEmailComposer(), usage, new AiPricing(), new FakeAiBudget { State = BudgetState.Warning });

        await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), workspaceId);

        var entry = Assert.Single(usage.Entries);
        Assert.Equal(AiPurpose.Compose, entry.Purpose);
        Assert.True(entry.Succeeded);
    }
}
