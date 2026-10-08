namespace ZenLead.Application.Abstractions;

/// <param name="Number">1-based data record number (the header is row 0).</param>
public record CsvRow(int Number, IReadOnlyDictionary<string, string> Fields);
public record CsvPreview(string Delimiter, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows, int RowCount);
public record CsvRowOrError(int Number, CsvRow? Row, string? Error);

/// <summary>Not a CSV / no header / binary / too many columns / too many rows. The message is safe to show to the user.</summary>
public class CsvFormatException(string message) : Exception(message);

public interface ICsvRowReader
{
    /// <summary>Reads the whole stream once: validates it is text CSV, detects delimiter, counts data records, returns the first rows.</summary>
    Task<CsvPreview> PreviewAsync(Stream stream, int sampleRows, int maxRows, CancellationToken ct = default);

    /// <summary>Streams data records. Malformed records come back with a null <see cref="CsvRowOrError.Row"/> and an error message rather than throwing.</summary>
    IAsyncEnumerable<CsvRowOrError> ReadAsync(Stream stream, string delimiter, CancellationToken ct = default);
}
