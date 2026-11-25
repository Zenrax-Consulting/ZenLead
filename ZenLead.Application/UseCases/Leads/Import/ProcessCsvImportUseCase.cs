using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Csv;
using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Leads.Import;

/// <summary>The job body. Rows go to <see cref="LeadIngestionService"/> in batches; nothing here validates or dedupes.</summary>
public class ProcessCsvImportUseCase(ICsvImportRepository batches, IBlobStorage blobs, ICsvRowReader reader, LeadIngestionService ingestion, TimeProvider clock)
{
    public const int BatchSize = 500, MaxLoggedIssues = 1000;

    public async Task ExecuteAsync(Guid batchId, Guid workspaceId, CancellationToken ct)
    {
        var batch = await batches.GetForJobAsync(batchId, workspaceId, ct);             // explicit workspace scope — no HTTP context here
        if (batch is null || batch.Status is CsvImportStatus.Completed) return;        // idempotent re-run

        ColumnMapping? mapping = null;
        try { mapping = JsonSerializer.Deserialize<ColumnMapping>(batch.ColumnMappingJson ?? "", CsvJson.Options); }
        catch (JsonException) { }
        if (mapping is null) { await FailAsync(batch, "Column mapping missing.", ct); return; }

        await using var stream = await blobs.OpenReadAsync(batch.BlobPath, ct);
        if (stream is null) { await FailAsync(batch, "Uploaded file is no longer available. Upload it again.", ct); return; }

        batch.Status = CsvImportStatus.Parsing; batch.FailureReason = null;
        var issues = CsvIssueLog.Load(batch.ErrorLogJson, MaxLoggedIssues);
        var pending = new List<(int Number, CandidateLead Candidate)>(BatchSize);
        var skip = batch.ProcessedRowCount;                                              // resume point
        var last = skip;

        await foreach (var item in reader.ReadAsync(stream, batch.Delimiter, ct))
        {
            if (item.Number <= skip) continue;
            last = item.Number;
            if (item.Row is null)
            {
                // persisted by the next flush, whose checkpoint is never behind this row
                batch.ErrorCount++;
                issues.Add(new(item.Number, IngestionOutcome.Invalid, item.Error ?? "Malformed row", null, null));
                continue;
            }

            pending.Add((item.Number, CsvRowMapper.Map(item.Row.Fields, mapping)));
            if (pending.Count == BatchSize) await FlushAsync(batch, workspaceId, pending, issues, ct);
        }
        if (pending.Count > 0) await FlushAsync(batch, workspaceId, pending, issues, ct);

        batch.ProcessedRowCount = Math.Max(last, batch.ProcessedRowCount);
        batch.Status = CsvImportStatus.Completed; batch.FinishedAt = clock.GetUtcNow().UtcDateTime;
        batch.ErrorLogJson = issues.ToJson();
        await batches.UpdateAsync(batch, ct);
    }

    private async Task FlushAsync(CsvImportBatch batch, Guid workspaceId, List<(int Number, CandidateLead Candidate)> pending, CsvIssueLog issues, CancellationToken ct)
    {
        var summary = await ingestion.IngestAsync(workspaceId, pending.Select(p => p.Candidate).ToList(),
                                                  new IngestionSource(LeadSource.Csv, batch.Id), ct);
        foreach (var r in summary.Rows.Where(r => r.Outcome != IngestionOutcome.Imported))
        {
            var (number, c) = pending[r.Index];
            issues.Add(new(number, r.Outcome, r.Reason ?? r.Outcome.ToString(), c.Email, c.Name));
        }
        batch.ImportedCount += summary.Imported;
        batch.SkippedDuplicateCount += summary.Duplicates;
        batch.SkippedSuppressedCount += summary.Suppressed;
        batch.ErrorCount += summary.Invalid;
        batch.ProcessedRowCount = pending[^1].Number;
        batch.ErrorLogJson = issues.ToJson();
        pending.Clear();
        await batches.UpdateAsync(batch, ct);                                            // one commit per batch → progress is visible and resumable
    }

    private async Task FailAsync(CsvImportBatch batch, string reason, CancellationToken ct)
    {
        batch.Status = CsvImportStatus.Failed; batch.FailureReason = reason; batch.FinishedAt = clock.GetUtcNow().UtcDateTime;
        await batches.UpdateAsync(batch, ct);
    }
}
