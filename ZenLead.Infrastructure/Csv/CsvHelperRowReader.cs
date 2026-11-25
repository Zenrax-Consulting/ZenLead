using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Csv;

public class CsvHelperRowReader : ICsvRowReader
{
    public const int MaxColumns = 100;

    private static readonly char[] Candidates = [',', ';', '\t', '|'];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private const int HeadBytes = 4096, SniffChars = 8192;

    public async Task<CsvPreview> PreviewAsync(Stream stream, int sampleRows, int maxRows, CancellationToken ct = default)
    {
        stream = await EnsureSeekableAsync(stream, ct);
        var (encoding, delimiter) = await ProbeAsync(stream, ct);

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, Config(delimiter, _ => { }));
        if (!await csv.ReadAsync()) throw new CsvFormatException("The file is empty.");
        csv.ReadHeader();
        var headers = ReadHeaders(csv);

        var sample = new List<IReadOnlyList<string>>();
        var count = 0;
        await foreach (var item in IterateAsync(csv, headers, () => false, ct))
        {
            count++;
            if (count > maxRows) throw new CsvFormatException($"The file has more than {maxRows:N0} rows. Split it and import in parts.");
            if (item.Row is not null && sample.Count < sampleRows) sample.Add(headers.Select(h => item.Row.Fields[h]).ToList());
        }
        if (count == 0) throw new CsvFormatException("The file has a header but no data rows.");
        return new CsvPreview(delimiter.ToString(), headers, sample, count);
    }

    public async IAsyncEnumerable<CsvRowOrError> ReadAsync(Stream stream, string delimiter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        stream = await EnsureSeekableAsync(stream, ct);
        var (encoding, _) = await ProbeAsync(stream, ct);          // the delimiter was recorded at upload; only the encoding is re-detected

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var bad = false;
        using var csv = new CsvReader(reader, Config(delimiter.Length == 1 ? delimiter[0] : ',', _ => bad = true));
        if (!await csv.ReadAsync()) yield break;
        csv.ReadHeader();
        var headers = ReadHeaders(csv);

        await foreach (var item in IterateAsync(csv, headers, () => { var b = bad; bad = false; return b; }, ct))
            yield return item;
    }

    /// <summary>Walks data records. A record with bad quoting is reported, not thrown; if the parser cannot move past it the walk ends there.</summary>
    private static async IAsyncEnumerable<CsvRowOrError> IterateAsync(CsvReader csv, IReadOnlyList<string> headers, Func<bool> takeBadData, [EnumeratorCancellation] CancellationToken ct)
    {
        var number = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var before = csv.Parser.CharCount;
            bool has;
            string? error = null;
            try { has = await csv.ReadAsync(); }
            catch (CsvHelperException) { has = true; error = "Malformed quoting"; }
            if (!has) yield break;

            number++;
            if (takeBadData()) error ??= "Malformed quoting";
            // CsvHelper tolerates a stray quote and lets an unterminated one swallow the rest of the file as a single field;
            // valid records always have an even number of quote characters (escaped quotes come in pairs)
            if (error is null && HasUnbalancedQuotes(csv.Parser.RawRecord)) error = "Malformed quoting";
            if (error is not null)
            {
                yield return new CsvRowOrError(number, null, error);
                if (csv.Parser.CharCount == before) yield break;       // no progress after an exception: stop rather than loop forever
                continue;
            }

            var fields = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) fields[headers[i]] = i < csv.Parser.Count ? csv.Parser[i] ?? "" : "";
            yield return new CsvRowOrError(number, new CsvRow(number, fields), null);
        }
    }

    private static bool HasUnbalancedQuotes(string raw)
    {
        var quotes = 0;
        foreach (var ch in raw) if (ch == '"') quotes++;
        return quotes % 2 != 0;
    }

    private static List<string> ReadHeaders(CsvReader csv)
    {
        var headers = csv.HeaderRecord!.Select(h => h.Trim()).ToList();
        if (headers.Count > MaxColumns) throw new CsvFormatException($"The file has more than {MaxColumns} columns.");
        if (headers.Any(string.IsNullOrWhiteSpace))
            throw new CsvFormatException("The header row has a blank column name. Give every column a name and upload again.");
        var duplicates = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"\"{g.Key}\"").ToList();
        if (duplicates.Count > 0)
            throw new CsvFormatException($"The header row repeats column names: {string.Join(", ", duplicates)}. Column names must be unique.");
        return headers;
    }

    private static CsvConfiguration Config(char delimiter, Action<BadDataFoundArgs> onBadData) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter.ToString(),
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = args => onBadData(args),
        DetectDelimiter = false,
        PrepareHeaderForMatch = a => a.Header.Trim()
    };

    private static async Task<Stream> EnsureSeekableAsync(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek) { stream.Position = 0; return stream; }
        var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }

    /// <summary>One pass over the stream: binary check, encoding (BOM wins, then strict UTF-8, then Latin-1) and delimiter. Leaves the stream at 0.</summary>
    private static async Task<(Encoding Encoding, char Delimiter)> ProbeAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[HeadBytes];
        var read = 0;
        while (read < head.Length)
        {
            var n = await stream.ReadAsync(head.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }

        var bom = BomEncoding(head.AsSpan(0, read));
        if (bom is null && head.AsSpan(0, read).Contains((byte)0)) throw new CsvFormatException("That doesn't look like a text CSV file.");

        var encoding = bom ?? await DetectEncodingAsync(stream, ct);

        stream.Position = 0;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var chars = new char[SniffChars];
        var count = await reader.ReadBlockAsync(chars.AsMemory(), ct);
        var delimiter = SniffDelimiter(new string(chars, 0, count));

        stream.Position = 0;
        return (encoding, delimiter);
    }

    private static Encoding? BomEncoding(ReadOnlySpan<byte> head) => head switch
    {
        [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true),
        [0xFF, 0xFE, ..] => Encoding.Unicode,
        [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
        _ => null
    };

    /// <summary>Strict UTF-8 over the whole file (a stray Latin-1 byte can sit far below the first 4 KB); anything that is not valid UTF-8 is read as Latin-1.</summary>
    private static async Task<Encoding> DetectEncodingAsync(Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var decoder = StrictUtf8.GetDecoder();
        var buffer = new byte[64 * 1024];
        var chars = new char[StrictUtf8.GetMaxCharCount(buffer.Length)];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                decoder.GetChars(buffer, 0, n, chars, 0, flush: false);
            decoder.GetChars([], 0, 0, chars, 0, flush: true);       // a truncated multi-byte sequence at EOF is invalid too
            return StrictUtf8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Counts each candidate in the first non-empty line, ignoring anything inside quotes. Highest count wins; ties and no match default to a comma.</summary>
    internal static char SniffDelimiter(string head)
    {
        var counts = new int[Candidates.Length];
        var inQuotes = false;
        var hasContent = false;
        foreach (var ch in head)
        {
            if (ch == '"') { inQuotes = !inQuotes; hasContent = true; continue; }
            if (inQuotes) continue;
            if (ch is '\n' or '\r')
            {
                if (hasContent) break;
                continue;
            }
            var idx = Array.IndexOf(Candidates, ch);
            if (idx >= 0) counts[idx]++;
            if (!char.IsWhiteSpace(ch) || ch == '\t') hasContent = true;
        }
        var best = 0;
        for (var i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        return counts[best] == 0 ? ',' : Candidates[best];
    }
}
