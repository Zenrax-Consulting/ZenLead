using ZenLead.Application.Csv;
using ZenLead.Application.Leads;

namespace ZenLead.Tests.Application.Csv;

public class ErrorCsvWriterTests
{
    private static CsvRowIssue Issue(string reason, string? email = "a@x.com", string? name = "Ann", int row = 1)
        => new(row, IngestionOutcome.Invalid, reason, email, name);

    [Fact]
    public void HeaderAndPlainRow()
    {
        var csv = ErrorCsvWriter.Build([Issue("Invalid email address", row: 7)], 1);

        Assert.Equal("row,outcome,reason,email,name\r\n7,Invalid,Invalid email address,a@x.com,Ann\r\n", csv);
    }

    [Fact]
    public void CommasQuotesAndNewlines_AreQuoted()
    {
        var csv = ErrorCsvWriter.Build([Issue("bad, \"really\"", name: "Line1\nLine2")], 1);

        Assert.Contains("\"bad, \"\"really\"\"\"", csv);
        Assert.Contains("\"Line1\nLine2\"", csv);
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("@SUM(1+1)")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("\tTabbed")]
    public void FormulaCells_ArePrefixedWithAnApostrophe(string hostile)
    {
        var csv = ErrorCsvWriter.Build([Issue("reason", email: hostile, name: hostile)], 1);

        var dataLine = csv.Split("\r\n")[1];
        Assert.DoesNotContain("," + hostile, dataLine);
        Assert.Contains("'" + hostile.Replace("\"", "\"\""), csv);
    }

    [Fact]
    public void ReasonStartingWithAFormulaCharacter_IsGuardedToo()
    {
        var csv = ErrorCsvWriter.Build([Issue("=HYPERLINK(\"http://evil\")")], 1);

        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.DoesNotContain(",\"=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Fact]
    public void NullCells_AreEmpty()
        => Assert.Equal("row,outcome,reason,email,name\r\n3,Invalid,Malformed quoting,,\r\n",
            ErrorCsvWriter.Build([new CsvRowIssue(3, IngestionOutcome.Invalid, "Malformed quoting", null, null)], 1));

    [Fact]
    public void WhenMoreIssuesExistThanAreLogged_ALastRowSaysSo()
    {
        var csv = ErrorCsvWriter.Build([Issue("x")], 2500);

        Assert.Contains("Only the first 1 issues are listed (2,500 in total)", csv);
    }

    [Fact]
    public void WhenEverythingIsListed_NoNoteIsAdded()
        => Assert.DoesNotContain("Only the first", ErrorCsvWriter.Build([Issue("x")], 1));
}
