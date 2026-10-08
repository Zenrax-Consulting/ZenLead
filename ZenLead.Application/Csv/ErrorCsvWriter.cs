using System.Text;

namespace ZenLead.Application.Csv;

/// <summary>Builds the downloadable issues report. Cells that a spreadsheet would treat as formulas are neutralised.</summary>
public static class ErrorCsvWriter
{
    public static string Build(IReadOnlyList<CsvRowIssue> issues, int totalIssues)
    {
        var sb = new StringBuilder("row,outcome,reason,email,name\r\n");
        foreach (var i in issues)
            sb.Append(Cell(i.Row.ToString())).Append(',').Append(Cell(i.Outcome.ToString())).Append(',')
              .Append(Cell(i.Reason)).Append(',').Append(Cell(i.Email)).Append(',').Append(Cell(i.Name)).Append("\r\n");

        if (totalIssues > issues.Count)
            sb.Append(",,").Append(Cell($"Only the first {issues.Count:N0} issues are listed ({totalIssues:N0} in total)")).Append(",,\r\n");
        return sb.ToString();
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;   // formula-injection guard
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
