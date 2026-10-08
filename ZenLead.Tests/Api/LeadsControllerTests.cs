using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Leads;
using ZenLead.Application.Validation.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Leads;

namespace ZenLead.Tests.Api;

public class FakeLeadRepository : ILeadRepository
{
    private readonly Dictionary<Guid, Lead> _leads = [];
    public void Seed(Lead lead) => _leads[lead.Id] = lead;
    public bool Exists(Guid id) => _leads.ContainsKey(id);
    public Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default) { _leads[lead.Id] = lead; return Task.FromResult(lead); }
    public Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_leads.GetValueOrDefault(id));
    public Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery query, CancellationToken ct = default)
    {
        var all = _leads.Values.Where(l => l.WorkspaceId == workspaceId).ToList();
        return Task.FromResult(new PagedResult<Lead>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
    }
    public Task<IReadOnlyList<Lead>> GetByIdsAsync(Guid workspaceId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Lead>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId && ids.Contains(l.Id)).ToList());
    public Task<IReadOnlyList<Guid>> ListIdsAsync(Guid workspaceId, LeadQuery query, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Guid>>(_leads.Values.Where(l => l.WorkspaceId == workspaceId).Select(l => l.Id).Take(max).ToList());
    public Task UpdateAsync(Lead lead, CancellationToken ct = default) => Task.CompletedTask;
    public Task SoftDeleteAsync(Lead lead, CancellationToken ct = default) { _leads.Remove(lead.Id); return Task.CompletedTask; }
}

public class LeadsControllerTests
{
    private static LeadsController BuildController(FakeLeadRepository repo, Guid callerWorkspaceId)
    {
        var store = new FakeLeadIngestionStore { OnInserted = repo.Seed };
        var controller = new LeadsController(repo, new LeadIngestionService(store), store,
            new CreateLeadRequestValidator(), new UpdateLeadRequestValidator(), new LeadQueryValidator());
        var identity = new ClaimsIdentity([new Claim("workspace_id", callerWorkspaceId.ToString())], "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static Lead NewLead(Guid workspaceId, LeadStatus status = LeadStatus.New) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Jane", Email = "jane@acme.com",
        Status = status, CreatedAt = DateTime.UtcNow
    };

    private static UpdateLeadRequest Update(LeadStatus status) => new("Jane", null, status, null, null);

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

    [Fact]
    public async Task Update_AndDelete_OtherWorkspacesLead_ReturnNotFound()
    {
        var repo = new FakeLeadRepository();
        var lead = NewLead(Guid.NewGuid());
        repo.Seed(lead);
        var controller = BuildController(repo, Guid.NewGuid());

        var put = await controller.Update(lead.Id, Update(LeadStatus.Contacted), CancellationToken.None);
        var delete = await controller.Delete(lead.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(put.Result);
        Assert.IsType<NotFoundResult>(delete);
        Assert.True(repo.Exists(lead.Id));
        Assert.Equal(LeadStatus.New, lead.Status);
    }

    [Fact]
    public async Task Update_ValidTransition_Succeeds_InvalidTransition_Conflicts()
    {
        var ws = Guid.NewGuid();
        var repo = new FakeLeadRepository();
        var lead = NewLead(ws);
        repo.Seed(lead);
        var controller = BuildController(repo, ws);

        var ok = await controller.Update(lead.Id, Update(LeadStatus.Contacted), CancellationToken.None);
        var back = await controller.Update(lead.Id, Update(LeadStatus.New), CancellationToken.None);

        Assert.IsType<OkObjectResult>(ok.Result);
        Assert.IsType<ConflictObjectResult>(back.Result);
        Assert.Equal(LeadStatus.Contacted, lead.Status);
    }

    [Fact]
    public async Task Update_WithCompany_LinksIt_AndWithoutClearsIt()
    {
        var ws = Guid.NewGuid();
        var repo = new FakeLeadRepository();
        var lead = NewLead(ws);
        repo.Seed(lead);
        var controller = BuildController(repo, ws);

        await controller.Update(lead.Id, new("Jane", null, LeadStatus.New, "Acme", "acme.com"), CancellationToken.None);
        Assert.NotNull(lead.CompanyId);

        await controller.Update(lead.Id, new("Jane", null, LeadStatus.New, null, null), CancellationToken.None);
        Assert.Null(lead.CompanyId);
    }

    [Fact]
    public async Task Create_SameEmailTwice_SecondIsConflict()
    {
        var ws = Guid.NewGuid();
        var controller = BuildController(new FakeLeadRepository(), ws);
        var request = new CreateLeadRequest("Jane", "jane@acme.com", null);

        var first = await controller.Create(request, CancellationToken.None);
        var second = await controller.Create(request with { Email = "JANE@acme.com" }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.IsType<ConflictObjectResult>(second.Result);
    }

    [Fact]
    public async Task List_ReturnsPagedShape_ScopedToCallersWorkspace()
    {
        var ws = Guid.NewGuid();
        var repo = new FakeLeadRepository();
        repo.Seed(NewLead(ws));
        repo.Seed(NewLead(Guid.NewGuid()));
        var controller = BuildController(repo, ws);

        var result = await controller.List(new LeadQuery(), CancellationToken.None);

        var page = Assert.IsType<PagedResult<LeadResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task List_InvalidQuery_ReturnsValidationProblem()
    {
        var controller = BuildController(new FakeLeadRepository(), Guid.NewGuid());

        var result = await controller.List(new LeadQuery(PageSize: 1000), CancellationToken.None);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.NotEqual(200, ((ObjectResult)result.Result!).StatusCode);
    }
}
