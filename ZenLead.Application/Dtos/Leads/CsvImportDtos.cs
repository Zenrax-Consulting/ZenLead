using ZenLead.Application.Csv;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CsvUploadResult(Guid BatchId, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows, int RowCount);

public record CsvUploadResponse(Guid BatchId, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows, int RowCount, ColumnMappingGuess SuggestedMapping);

public record ColumnMappingRequest(string? Name, string? FirstName, string? LastName, string? Email,
    string? Title, string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize)
{
    public ColumnMapping ToMapping() => new(Name, FirstName, LastName, Email ?? "", Title, CompanyName, CompanyDomain, Industry, Country, CompanySize);
}

public record ImportStatusResponse(Guid Id, string FileName, CsvImportStatus Status, int RowCount, int ProcessedRowCount, int ImportedCount,
    int SkippedDuplicateCount, int SkippedSuppressedCount, int ErrorCount, string? FailureReason, int Percent)
{
    public static ImportStatusResponse From(CsvImportBatch b) => new(b.Id, b.FileName, b.Status, b.RowCount, b.ProcessedRowCount, b.ImportedCount,
        b.SkippedDuplicateCount, b.SkippedSuppressedCount, b.ErrorCount, b.FailureReason,
        b.Status == CsvImportStatus.Completed ? 100 : b.RowCount == 0 ? 0 : Math.Min(100, (int)(b.ProcessedRowCount * 100L / b.RowCount)));
}
