using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/companies")]
public class CompaniesController(ICompanyRepository companies) : ControllerBase
{
    private const int MaxResults = 20;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CompanySummary>>> Search([FromQuery] string? q, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        if (q is { Length: > 100 }) return BadRequest(new { message = "q must be 100 characters or fewer." });
        return Ok(await companies.SearchAsync(workspaceId, q, MaxResults, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CompanySummary>> GetById(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var company = await companies.GetByIdAsync(workspaceId, id, ct);
        return company is null ? NotFound() : Ok(company);
    }
}
