using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Validation.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Api;

public class FakeLeadRepository : ILeadRepository
{
    private readonly Dictionary<Guid, Lead> _leads = [];
    public void Seed(Lead lead) => _leads[lead.Id] = lead;
    public Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default) { _leads[lead.Id] = lead; return Task.FromResult(lead); }
    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_leads.GetValueOrDefault(id));
    public Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Lead>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList());
}

public class LeadsControllerTests
{
    private static LeadsController BuildController(FakeLeadRepository repo, Guid callerWorkspaceId)
    {
        var controller = new LeadsController(repo, new CreateLeadRequestValidator());
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Lead NewLead(Guid workspaceId) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com",
        Status = LeadStatus.New, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GetById_LeadBelongsToDifferentWorkspace_ReturnsNotFound()
    {
        var repo = new FakeLeadRepository();
        var otherWorkspaceLead = NewLead(Guid.NewGuid());
        repo.Seed(otherWorkspaceLead);

        var controller = BuildController(repo, Guid.NewGuid()); // caller is in a *different* workspace

        var result = await controller.GetById(otherWorkspaceLead.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetById_LeadBelongsToCallersWorkspace_ReturnsIt()
    {
        var workspaceId = Guid.NewGuid();
        var repo = new FakeLeadRepository();
        var lead = NewLead(workspaceId);
        repo.Seed(lead);

        var controller = BuildController(repo, workspaceId);

        var result = await controller.GetById(lead.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(lead.Id, Assert.IsType<LeadResponse>(ok.Value).Id);
    }
}
