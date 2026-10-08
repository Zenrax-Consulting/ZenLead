using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Dtos.Discovery;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Application.Validation.Discovery;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Discovery;

namespace ZenLead.Tests.Api;

public class TargetProfilesControllerTests
{
    private readonly FakeTargetProfileRepository _profiles = new();
    private readonly FakeLeadRepository _leads = new();
    private readonly FakeDiscoveryRunRepository _runs = new();
    private readonly FakeJobScheduler _scheduler;
    private readonly LeadSourceOptions _options = new();

    public TargetProfilesControllerTests() => _scheduler = new FakeJobScheduler(_runs);

    private TargetProfilesController Controller(Guid workspaceId, Guid? userId = null)
    {
        var useCase = new StartDiscoveryRunUseCase(_profiles, _runs, new ScriptedLeadSource(), _scheduler, _options, TimeProvider.System);
        var controller = new TargetProfilesController(_profiles, _leads, useCase,
            new TargetProfileRequestValidator(), new SuggestFromLeadsRequestValidator(), new StartRunRequestValidator(_options));
        var identity = new ClaimsIdentity([new Claim("workspace_id", workspaceId.ToString()), new Claim("sub", (userId ?? Guid.NewGuid()).ToString())], "test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    private static CriteriaDto Criteria(params string[] countries) => new([], [], countries, null, null, []);

    private static Lead NewLead(Guid ws, LeadStatus status = LeadStatus.New, string? title = "CTO") => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Jane", Email = $"{Guid.NewGuid()}@acme.com", Title = title, Status = status, CreatedAt = DateTime.UtcNow,
        Company = new Company { Industry = "Software", Country = "US", Size = "51-200" }
    };

    [Fact]
    public async Task Crud_HappyPath()
    {
        var ws = Guid.NewGuid();
        var controller = Controller(ws);

        var created = await controller.Create(new TargetProfileRequest(" US CTOs ", Criteria("US")), CancellationToken.None);
        var response = Assert.IsType<TargetProfileResponse>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        var updated = await controller.Update(response.Id, new TargetProfileRequest("Renamed", Criteria("GB")), CancellationToken.None);
        var list = await controller.List(CancellationToken.None);
        var deleted = await controller.Delete(response.Id, CancellationToken.None);

        Assert.Equal("US CTOs", response.Name);
        Assert.Equal(["US"], response.Criteria.Countries);
        Assert.Equal("Renamed", Assert.IsType<TargetProfileResponse>(Assert.IsType<OkObjectResult>(updated.Result).Value).Name);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TargetProfileResponse>>(Assert.IsType<OkObjectResult>(list.Result).Value));
        Assert.IsType<NoContentResult>(deleted);
        Assert.Empty(_profiles.Profiles);
    }

    [Fact]
    public async Task EmptyCriteria_IsRejected()
    {
        var result = await Controller(Guid.NewGuid()).Create(new TargetProfileRequest("Empty", new CriteriaDto([], [" "], [], null, null, [])), CancellationToken.None);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.IsAssignableFrom<ValidationProblemDetails>(((ObjectResult)result.Result!).Value);
        Assert.Empty(_profiles.Profiles);
    }

    [Fact]
    public async Task OtherWorkspace_CannotUpdateDeleteOrRun()
    {
        var owner = Guid.NewGuid();
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = owner, Name = "Mine", CriteriaJson = "{}" };
        _profiles.Profiles.Add(profile);
        var intruder = Controller(Guid.NewGuid());

        var put = await intruder.Update(profile.Id, new TargetProfileRequest("Hacked", Criteria("US")), CancellationToken.None);
        var delete = await intruder.Delete(profile.Id, CancellationToken.None);
        var run = await intruder.StartRun(profile.Id, new StartRunRequest(10), CancellationToken.None);

        Assert.IsType<NotFoundResult>(put.Result);
        Assert.IsType<NotFoundResult>(delete);
        Assert.IsType<NotFoundResult>(run.Result);
        Assert.Equal("Mine", profile.Name);
        Assert.Single(_profiles.Profiles);
        Assert.Empty(_scheduler.Enqueued);
    }

    [Fact]
    public async Task StartRun_Accepted_AndSecondStartConflicts()
    {
        var ws = Guid.NewGuid();
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "P", CriteriaJson = "{}" };
        _profiles.Profiles.Add(profile);
        var controller = Controller(ws);

        var first = await controller.StartRun(profile.Id, new StartRunRequest(10), CancellationToken.None);
        var second = await controller.StartRun(profile.Id, new StartRunRequest(10), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(first.Result);
        Assert.StartsWith("/api/v1/discovery/runs/", accepted.Location);
        Assert.IsType<ConflictObjectResult>(second.Result);
    }

    [Fact]
    public async Task StartRun_MaxLeadsOutOfRange_IsRejected()
    {
        var ws = Guid.NewGuid();
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "P", CriteriaJson = "{}" };
        _profiles.Profiles.Add(profile);

        var result = await Controller(ws).StartRun(profile.Id, new StartRunRequest(_options.MaxLeadsPerRun + 1), CancellationToken.None);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Empty(_scheduler.Enqueued);
    }

    [Fact]
    public async Task FromLeads_IgnoresOtherWorkspacesLeads_AndNothingIsSaved()
    {
        var ws = Guid.NewGuid();
        var mine = NewLead(ws);
        var theirs = NewLead(Guid.NewGuid(), title: "Janitor");
        _leads.Seed(mine); _leads.Seed(theirs);

        var result = await Controller(ws).FromLeads(new SuggestFromLeadsRequest([mine.Id, theirs.Id]), CancellationToken.None);

        var suggestion = Assert.IsType<SuggestedProfileResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["CTO"], suggestion.Criteria.JobTitles);
        Assert.Equal("Similar to Jane", suggestion.Name);
        Assert.Equal(mine.Id, suggestion.SourceLeadId);
        Assert.Empty(_profiles.Profiles);
    }

    [Fact]
    public async Task FromLeads_OnlyOtherWorkspacesLeads_IsNotFound()
    {
        var theirs = NewLead(Guid.NewGuid());
        _leads.Seed(theirs);

        var result = await Controller(Guid.NewGuid()).FromLeads(new SuggestFromLeadsRequest([theirs.Id]), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task FromLeads_OnlyRepliedWithNoReplies_IsBadRequest()
    {
        var ws = Guid.NewGuid();
        var lead = NewLead(ws);
        _leads.Seed(lead);

        var result = await Controller(ws).FromLeads(new SuggestFromLeadsRequest([lead.Id], OnlyReplied: true), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
