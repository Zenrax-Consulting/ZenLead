using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/leads")]
public class LeadsController(ILeadRepository leads, IValidator<CreateLeadRequest> createValidator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeadResponse>>> List(CancellationToken ct)
    {
        var workspaceId = GetWorkspaceId();
        var result = await leads.ListByWorkspaceAsync(workspaceId, ct);
        return Ok(result.Select(ToResponse));
    }

    [HttpPost]
    public async Task<ActionResult<LeadResponse>> Create(CreateLeadRequest request, CancellationToken ct)
    {
        var validation = await createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var modelState = new ModelStateDictionary();
            foreach (var error in validation.Errors)
                modelState.AddModelError(error.PropertyName, error.ErrorMessage);
            return ValidationProblem(modelState);
        }

        var lead = new Lead
        {
            Id = Guid.NewGuid(),
            WorkspaceId = GetWorkspaceId(),
            Name = request.Name,
            Email = request.Email,
            Title = request.Title,
            Status = LeadStatus.New,
            CreatedAt = DateTime.UtcNow
        };
        var created = await leads.CreateAsync(lead, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(created));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LeadResponse>> GetById(Guid id, CancellationToken ct)
    {
        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != GetWorkspaceId())
            return NotFound(); // same response whether missing or wrong workspace — don't leak existence across tenants

        return Ok(ToResponse(lead));
    }

    private Guid GetWorkspaceId()
        => Guid.Parse(User.FindFirstValue("workspace_id")!);

    private static LeadResponse ToResponse(Lead l)
        => new(l.Id, l.Name, l.Email, l.Title, l.Status, l.CreatedAt);
}
