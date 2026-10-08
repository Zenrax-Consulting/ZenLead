using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/leads")]
public class LeadsController(
    ILeadRepository leads,
    LeadIngestionService ingestion,
    ILeadIngestionStore ingestionStore,
    IValidator<CreateLeadRequest> createValidator,
    IValidator<UpdateLeadRequest> updateValidator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeadResponse>>> List(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var result = await leads.ListByWorkspaceAsync(workspaceId, ct);
        return Ok(result.Select(ToResponse));
    }

    [HttpPost]
    public async Task<ActionResult<LeadResponse>> Create(CreateLeadRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();

        var validation = await createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(validation.ToModelState());

        var summary = await ingestion.IngestAsync(workspaceId,
            [new CandidateLead(request.Name, request.Email, request.Title, request.CompanyName, request.CompanyDomain, null, null)],
            new IngestionSource(LeadSource.Manual, null), ct);
        var row = summary.Rows[0];

        switch (row.Outcome)
        {
            case IngestionOutcome.Imported:
                var created = await leads.GetByIdAsync(row.LeadId!.Value, ct);
                return CreatedAtAction(nameof(GetById), new { id = row.LeadId }, ToResponse(created!));
            case IngestionOutcome.Invalid:
                var modelState = new ModelStateDictionary();
                modelState.AddModelError(nameof(request.Email), row.Reason!);
                return ValidationProblem(modelState);
            default: // Duplicate / Suppressed
                return Conflict(new { message = row.Reason });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LeadResponse>> GetById(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();

        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != workspaceId)
            return NotFound(); // same response whether missing or wrong workspace — don't leak existence across tenants

        return Ok(ToResponse(lead));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LeadResponse>> Update(Guid id, UpdateLeadRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();

        var validation = await updateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(validation.ToModelState());

        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return NotFound();
        if (!LeadStatusRules.CanTransition(lead.Status, request.Status))
            return Conflict(new { message = $"Cannot change status from {lead.Status} to {request.Status}." });

        // company change goes through the same find-or-create the importers use; no company fields clears the link
        var key = CompanyKey.From(request.CompanyName, request.CompanyDomain);
        if (key is null)
        {
            lead.CompanyId = null;
            lead.Company = null;
        }
        else
        {
            var ids = await ingestionStore.UpsertCompaniesAsync(workspaceId, [(key, null, null, null)], ct);
            if (lead.CompanyId != ids[key])
            {
                lead.CompanyId = ids[key];
                lead.Company = null; // reloaded below so the response carries the new company
            }
        }

        lead.Name = request.Name.Trim();
        lead.Title = request.Title?.Trim();
        lead.Status = request.Status;
        await leads.UpdateAsync(lead, ct);

        var updated = await leads.GetByIdAsync(id, ct);
        return Ok(ToResponse(updated ?? lead));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();

        var lead = await leads.GetByIdAsync(id, ct);
        if (lead is null || lead.WorkspaceId != workspaceId) return NotFound();

        await leads.SoftDeleteAsync(lead, ct);
        return NoContent();
    }

    private static LeadResponse ToResponse(Lead l)
        => new(l.Id, l.Name, l.Email, l.Title, l.Status, l.CreatedAt,
            l.Company is null ? null : new CompanySummary(l.Company.Id, l.Company.Name, l.Company.Domain, l.Company.Industry, l.Company.Country),
            l.Source, l.SourceRunId, l.EmailVerificationStatus);
}
