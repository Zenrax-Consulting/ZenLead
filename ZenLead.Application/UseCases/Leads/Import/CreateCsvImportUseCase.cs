using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Leads.Import;

/// <summary>Upload and preview: stores the file, validates it is a readable CSV and records a batch in <see cref="CsvImportStatus.Uploaded"/>.</summary>
public class CreateCsvImportUseCase(IBlobStorage blobs, ICsvRowReader reader, ICsvImportRepository batches, TimeProvider clock)
{
    public const int MaxRows = 50_000, SampleRows = 10;

    public async Task<CsvUploadResult> ExecuteAsync(Guid workspaceId, Guid userId, string fileName, Stream file, CancellationToken ct)
    {
        var batchId = Guid.NewGuid();
        var path = $"imports/{workspaceId}/{batchId}.csv";

        // the reader needs two passes (preview now, job later), so store once and preview from the stored copy
        await blobs.SaveAsync(path, file, "text/csv", ct);
        await using var stored = await blobs.OpenReadAsync(path, ct) ?? throw new InvalidOperationException("Blob not found after save.");

        CsvPreview preview;
        try { preview = await reader.PreviewAsync(stored, SampleRows, MaxRows, ct); }
        catch (CsvFormatException) { await blobs.DeleteAsync(path, ct); throw; }        // don't keep rejected files

        await batches.AddAsync(new CsvImportBatch
        {
            Id = batchId, WorkspaceId = workspaceId, FileName = Path.GetFileName(fileName), BlobPath = path,
            Delimiter = preview.Delimiter, RowCount = preview.RowCount, Status = CsvImportStatus.Uploaded,
            CreatedBy = userId, CreatedAt = clock.GetUtcNow().UtcDateTime
        }, ct);
        return new CsvUploadResult(batchId, preview.Headers, preview.SampleRows, preview.RowCount);
    }
}
