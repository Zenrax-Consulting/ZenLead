using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Csv;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Leads.Import;

public enum StartImportOutcome { NotFound, AlreadyStarted, InvalidMapping, Started }

public class StartImportResult
{
    public StartImportOutcome Outcome { get; private init; }
    public IReadOnlyList<string> Problems { get; private init; } = [];

    public static StartImportResult NotFound { get; } = new() { Outcome = StartImportOutcome.NotFound };
    public static StartImportResult AlreadyStarted { get; } = new() { Outcome = StartImportOutcome.AlreadyStarted };
    public static StartImportResult Started { get; } = new() { Outcome = StartImportOutcome.Started };
    public static StartImportResult InvalidMapping(IReadOnlyList<string> problems) => new() { Outcome = StartImportOutcome.InvalidMapping, Problems = problems };
}

/// <summary>Mapping and start. Headers are re-read from the stored file so the mapping is checked against the file, not what the client claims.</summary>
public class StartCsvImportUseCase(IBlobStorage blobs, ICsvRowReader reader, ICsvImportRepository batches, IJobScheduler scheduler, TimeProvider clock)
{
    public async Task<StartImportResult> ExecuteAsync(Guid workspaceId, Guid batchId, ColumnMapping mapping, CancellationToken ct)
    {
        var batch = await batches.GetAsync(batchId, ct);
        if (batch is null || batch.WorkspaceId != workspaceId) return StartImportResult.NotFound;
        if (batch.Status != CsvImportStatus.Uploaded) return StartImportResult.AlreadyStarted;      // start is once-only

        await using var stream = await blobs.OpenReadAsync(batch.BlobPath, ct);
        if (stream is null) return StartImportResult.InvalidMapping(["The uploaded file is no longer available. Upload it again."]);

        var headers = (await reader.PreviewAsync(stream, 0, CreateCsvImportUseCase.MaxRows, ct)).Headers;
        mapping = CsvRowMapper.Normalize(mapping, headers);
        var problems = CsvRowMapper.Validate(mapping, headers);
        if (problems.Count > 0) return StartImportResult.InvalidMapping(problems);

        batch.ColumnMappingJson = JsonSerializer.Serialize(mapping, CsvJson.Options);
        batch.Status = CsvImportStatus.Parsing;
        batch.StartedAt = clock.GetUtcNow().UtcDateTime;
        await batches.UpdateAsync(batch, ct);
        scheduler.EnqueueCsvImport(batch.Id, workspaceId);       // after the row is committed, so the job always finds it
        return StartImportResult.Started;
    }
}
