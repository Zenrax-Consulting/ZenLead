using System.Diagnostics;
using System.Text;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Csv;

namespace ZenLead.Tests.Infrastructure.Csv;

public class CsvHelperRowReaderTests
{
    private readonly CsvHelperRowReader _reader = new();

    private static MemoryStream Bytes(byte[] b) => new(b);
    private static MemoryStream Utf8(string s, bool bom = false) => Bytes([.. (bom ? Encoding.UTF8.GetPreamble() : []), .. Encoding.UTF8.GetBytes(s)]);

    private Task<CsvPreview> Preview(Stream s, int sample = 5, int max = 50_000) => _reader.PreviewAsync(s, sample, max);

    private async Task<List<CsvRowOrError>> ReadAll(Stream s, string delimiter = ",")
    {
        var rows = new List<CsvRowOrError>();
        await foreach (var r in _reader.ReadAsync(s, delimiter)) rows.Add(r);
        return rows;
    }

    // ---- encoding ----

    [Fact]
    public async Task Utf8WithBom_IsReadWithoutBomInFirstHeader()
    {
        var preview = await Preview(Utf8("Email,Name\r\na@x.com,Ann\r\n", bom: true));

        Assert.Equal(["Email", "Name"], preview.Headers);
    }

    [Fact]
    public async Task Utf8WithoutBom_KeepsAccentedAndCjkCharacters()
    {
        var rows = await ReadAll(Utf8("Email,Name\na@x.com,José Müller\nb@x.com,山田太郎\n"));

        Assert.Equal("José Müller", rows[0].Row!.Fields["Name"]);
        Assert.Equal("山田太郎", rows[1].Row!.Fields["Name"]);
    }

    [Fact]
    public async Task Latin1Bytes_AreDecodedAsLatin1()
    {
        var bytes = Encoding.Latin1.GetBytes("Email,Name\na@x.com,René\n");
        Assert.Contains((byte)0xE9, bytes);

        var rows = await ReadAll(Bytes(bytes));

        Assert.Equal("René", rows[0].Row!.Fields["Name"]);
    }

    [Fact]
    public async Task Latin1ByteFarBelowTheFirst4Kb_IsStillDetected()
    {
        var sb = new StringBuilder("Email,Name\n");
        for (var i = 0; i < 400; i++) sb.Append($"u{i}@x.com,Plain Name\n");
        sb.Append("last@x.com,René\n");

        var rows = await ReadAll(Bytes(Encoding.Latin1.GetBytes(sb.ToString())));

        Assert.Equal("René", rows[^1].Row!.Fields["Name"]);
    }

    [Fact]
    public async Task Utf16WithBom_IsAccepted_NotMistakenForBinary()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Email\tName\na@x.com\tAnn\n")).ToArray();

        var preview = await Preview(Bytes(bytes));

        Assert.Equal("\t", preview.Delimiter);
        Assert.Equal(["Email", "Name"], preview.Headers);
    }

    // ---- delimiter ----

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    [InlineData(",")]
    public async Task Delimiter_IsDetected(string delimiter)
    {
        var preview = await Preview(Utf8($"Email{delimiter}Name{delimiter}Title\na@x.com{delimiter}Ann{delimiter}CTO\n"));

        Assert.Equal(delimiter, preview.Delimiter);
        Assert.Equal(["Email", "Name", "Title"], preview.Headers);
    }

    [Fact]
    public async Task QuotedDelimiterInHeader_DoesNotFoolTheSniffer()
    {
        var preview = await Preview(Utf8("\"Name, Full\";Email;Title\nAnn;a@x.com;CTO\n"));

        Assert.Equal(";", preview.Delimiter);
        Assert.Equal(["Name, Full", "Email", "Title"], preview.Headers);
    }

    // ---- records ----

    [Fact]
    public async Task QuotedFieldWithNewline_CountsAsOneRecord()
    {
        var preview = await Preview(Utf8("Email,Note\na@x.com,\"line1\nline2\"\nb@x.com,ok\n"));
        var rows = await ReadAll(Utf8("Email,Note\na@x.com,\"line1\nline2\"\nb@x.com,ok\n"));

        Assert.Equal(2, preview.RowCount);
        Assert.Equal("line1\nline2", rows[0].Row!.Fields["Note"]);
        Assert.Equal(2, rows[1].Number);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task LineEndings_AreBothSupported_AndTrailingBlankLinesIgnored(string eol)
    {
        var text = $"Email,Name{eol}a@x.com,Ann{eol}b@x.com,Bob{eol}{eol}{eol}";

        var preview = await Preview(Utf8(text));
        var rows = await ReadAll(Utf8(text));

        Assert.Equal(2, preview.RowCount);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task MissingTrailingFields_AreEmptyStrings_AndExtraFieldsAreIgnored()
    {
        var rows = await ReadAll(Utf8("Email,Name,Title\na@x.com\nb@x.com,Bob,CTO,surplus,more\n"));

        Assert.Equal("", rows[0].Row!.Fields["Name"]);
        Assert.Equal("", rows[0].Row!.Fields["Title"]);
        Assert.Equal("CTO", rows[1].Row!.Fields["Title"]);
        Assert.Equal(3, rows[1].Row!.Fields.Count);
    }

    [Fact]
    public async Task Fields_AreTrimmed_AndLookupIsCaseInsensitive()
    {
        var rows = await ReadAll(Utf8("Email, Name \n  a@x.com  ,  Ann  \n"));

        Assert.Equal("a@x.com", rows[0].Row!.Fields["email"]);
        Assert.Equal("Ann", rows[0].Row!.Fields["Name"]);
    }

    [Fact]
    public async Task Preview_ReturnsOnlyTheRequestedSampleRows_ButCountsEverything()
    {
        var text = "Email\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"u{i}@x.com")) + "\n";

        var preview = await Preview(Utf8(text), sample: 10);

        Assert.Equal(30, preview.RowCount);
        Assert.Equal(10, preview.SampleRows.Count);
        Assert.Equal("u0@x.com", preview.SampleRows[0][0]);
    }

    // ---- malformed rows ----

    [Fact]
    public async Task MalformedQuote_IsReportedForThatRecord_AndLaterRecordsAreStillRead()
    {
        var text = "Email,Name\na@x.com,Ann\nb@x.com,Bo\"b\nc@x.com,Cy\nd@x.com,Di\n";

        var rows = await ReadAll(Utf8(text));
        var preview = await Preview(Utf8(text));

        Assert.Equal(4, rows.Count);
        Assert.Equal(4, preview.RowCount);
        Assert.NotNull(rows[0].Row);
        Assert.Null(rows[1].Row);
        Assert.Equal(2, rows[1].Number);
        Assert.False(string.IsNullOrWhiteSpace(rows[1].Error));
        Assert.Equal("c@x.com", rows[2].Row!.Fields["Email"]);
        Assert.Equal("d@x.com", rows[3].Row!.Fields["Email"]);
    }

    [Fact]
    public async Task UnterminatedQuote_DoesNotLoopForever_AndReportsARow()
    {
        var text = "Email,Name\na@x.com,Ann\nb@x.com,\"never closed\nc@x.com,Cy\n";

        var rows = await ReadAll(Utf8(text));

        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Row is null && r.Number == 2);
    }

    // ---- rejected files ----

    [Fact]
    public async Task EmptyFile_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8("")));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeaderOnly_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8("Email,Name\n")));
        Assert.Contains("no data rows", ex.Message);
    }

    [Fact]
    public async Task BlankHeaderCell_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8("Email,,Name\na@x.com,x,y\n")));
        Assert.Contains("blank", ex.Message);
    }

    [Fact]
    public async Task DuplicateHeaderNames_AreRejected_AndNamed()
    {
        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8("Email,Name,email\na@x.com,x,y\n")));
        Assert.Contains("\"Email\"", ex.Message);
    }

    [Fact]
    public async Task BinaryContent_IsRejected()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Bytes(png)));
        Assert.Contains("text CSV", ex.Message);
    }

    [Fact]
    public async Task MoreThanMaxRows_IsRejected()
    {
        var text = "Email\n" + string.Join("\n", Enumerable.Range(0, 11).Select(i => $"u{i}@x.com")) + "\n";

        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8(text), max: 10));
        Assert.Contains("more than 10 rows", ex.Message);
    }

    [Fact]
    public async Task ExactlyMaxRows_IsAccepted()
    {
        var text = "Email\n" + string.Join("\n", Enumerable.Range(0, 10).Select(i => $"u{i}@x.com")) + "\n";

        Assert.Equal(10, (await Preview(Utf8(text), max: 10)).RowCount);
    }

    [Fact]
    public async Task MoreThan100Columns_IsRejected()
    {
        var header = string.Join(",", Enumerable.Range(0, 101).Select(i => $"c{i}"));
        var row = string.Join(",", Enumerable.Range(0, 101).Select(i => "x"));

        var ex = await Assert.ThrowsAsync<CsvFormatException>(() => Preview(Utf8($"{header}\n{row}\n")));
        Assert.Contains("100 columns", ex.Message);
    }

    // ---- misc ----

    [Fact]
    public async Task NonSeekableStream_IsBufferedAndStillWorks()
    {
        await using var s = new NonSeekable(Utf8("Email\na@x.com\n"));

        Assert.Equal(1, (await Preview(s)).RowCount);
    }

    [Fact]
    public async Task TenThousandRows_PreviewAndFullRead_AreFast()
    {
        var sb = new StringBuilder("Email,First Name,Last Name,Company,Title\n");
        for (var i = 0; i < 10_000; i++) sb.Append($"user{i}@company{i % 50}.com,First{i},Last{i},Company {i % 50},Engineer\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        var sw = Stopwatch.StartNew();
        var preview = await Preview(Bytes(bytes));
        var rows = await ReadAll(Bytes(bytes));
        sw.Stop();

        Assert.Equal(10_000, preview.RowCount);
        Assert.Equal(10_000, rows.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
