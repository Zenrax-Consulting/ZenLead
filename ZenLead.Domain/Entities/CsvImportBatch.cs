using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class CsvImportBatch : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string FileName { get; set; } = string.Empty;       // display only; never used to build a path
    public string BlobPath { get; set; } = string.Empty;       // imports/{workspaceId}/{batchId}.csv
    public string? ColumnMappingJson { get; set; }
    public string Delimiter { get; set; } = ",";               // detected at upload
    public int RowCount { get; set; }                          // data records, counted at upload
    public int ProcessedRowCount { get; set; }                 // checkpoint
    public int ImportedCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    public int SkippedSuppressedCount { get; set; }
    public int ErrorCount { get; set; }                        // invalid rows
    public string? ErrorLogJson { get; set; }                  // capped list of CsvRowIssue
    public string? FailureReason { get; set; }
    public CsvImportStatus Status { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
