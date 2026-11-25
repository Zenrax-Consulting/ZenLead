using System.Text.Json;

namespace ZenLead.Application.Csv;

/// <summary>Keeps the first <c>max</c> non-imported rows so the batch row stays small; counters elsewhere hold the true totals.</summary>
public class CsvIssueLog
{
    private readonly List<CsvRowIssue> _issues;
    private readonly int _max;

    private CsvIssueLog(List<CsvRowIssue> issues, int max) { _issues = issues; _max = max; }

    public IReadOnlyList<CsvRowIssue> Issues => _issues;

    public static CsvIssueLog Load(string? json, int max)
    {
        if (string.IsNullOrWhiteSpace(json)) return new CsvIssueLog([], max);
        try { return new CsvIssueLog(JsonSerializer.Deserialize<List<CsvRowIssue>>(json, CsvJson.Options) ?? [], max); }
        catch (JsonException) { return new CsvIssueLog([], max); }
    }

    public void Add(CsvRowIssue issue)
    {
        if (_issues.Count < _max) _issues.Add(issue);
    }

    public string ToJson() => JsonSerializer.Serialize(_issues, CsvJson.Options);
}
