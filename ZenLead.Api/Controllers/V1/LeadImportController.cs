using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Csv;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.UseCases.Leads.Import;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/leads/import")]
public class LeadImportController(CreateCsvImportUseCase create, StartCsvImportUseCase start, ICsvImportRepository batches) : ControllerBase
{
    public const int MaxFileBytes = 10 * 1024 * 1024;

    [HttpPost]
    [EnableRateLimiting(RateLimiting.UploadPolicy)]
    [RequestSizeLimit(MaxFileBytes + 1024 * 1024)]                       // file + multipart overhead; Kestrel's 30 MB default is too generous
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFileBytes + 1024 * 1024)]
    public async Task<ActionResult<CsvUploadResponse>> Upload(IFormFile? file, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId || this.UserId() is not { } userId) return Unauthorized();
        if (file is null || file.Length == 0) return Problem("No file uploaded.", statusCode: StatusCodes.Status400BadRequest);
        if (file.Length > MaxFileBytes) return Problem("File is larger than 10 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (!string.Equals(Path.GetExtension(file.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
            return Problem("Only .csv files are accepted.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await create.ExecuteAsync(workspaceId, userId, file.FileName, stream, ct);
            return Ok(new CsvUploadResponse(result.BatchId, result.Headers, result.SampleRows, result.RowCount, ColumnGuesser.Guess(result.Headers)));
        }
        catch (CsvFormatException ex) { return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
    }

    [HttpPost("{batchId:guid}/start")]
    public async Task<IActionResult> Start(Guid batchId, ColumnMappingRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var result = await start.ExecuteAsync(workspaceId, batchId, request.ToMapping(), ct);
        return result.Outcome switch
        {
            StartImportOutcome.NotFound => NotFound(),
            StartImportOutcome.AlreadyStarted => Problem("This import has already been started.", statusCode: StatusCodes.Status409Conflict),
            StartImportOutcome.InvalidMapping => ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["mapping"] = result.Problems.ToArray() })),
            _ => Accepted()
        };
    }

    [HttpGet("{batchId:guid}/status")]
    public async Task<ActionResult<ImportStatusResponse>> Status(Guid batchId, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var batch = await batches.GetAsync(batchId, ct);
        return batch is null || batch.WorkspaceId != workspaceId ? NotFound() : Ok(ImportStatusResponse.From(batch));
    }

    [HttpGet("{batchId:guid}/errors")]
    public async Task<IActionResult> Errors(Guid batchId, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var batch = await batches.GetAsync(batchId, ct);
        if (batch is null || batch.WorkspaceId != workspaceId) return NotFound();

        var log = CsvIssueLog.Load(batch.ErrorLogJson, ProcessCsvImportUseCase.MaxLoggedIssues);
        var total = batch.ErrorCount + batch.SkippedDuplicateCount + batch.SkippedSuppressedCount;
        var csv = ErrorCsvWriter.Build(log.Issues, total);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"import-{batchId:N}-issues.csv");
    }
}
