using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Ai;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

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
        var sut = new ComposeEmailUseCase(leads, composer, new FakeTokenUsageTracker(), new AiPricing());

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
        var sut = new ComposeEmailUseCase(leads, new FakeEmailComposer(), new FakeTokenUsageTracker(), new AiPricing());

        var result = await sut.ExecuteAsync(new ComposeEmailRequest(lead.Id, null), Guid.NewGuid());

        Assert.Null(result);
    }
}
