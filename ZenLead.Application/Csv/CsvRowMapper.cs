using ZenLead.Application.Leads;

namespace ZenLead.Application.Csv;

public static class CsvRowMapper
{
    public static CandidateLead Map(IReadOnlyDictionary<string, string> fields, ColumnMapping m)
    {
        string? Get(string? header) => header is not null && fields.TryGetValue(header, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var name = Get(m.Name) ?? string.Join(' ', new[] { Get(m.FirstName), Get(m.LastName) }.Where(s => s is not null));
        return new CandidateLead(name, Get(m.Email), Get(m.Title), Get(m.CompanyName), Get(m.CompanyDomain),
                                 Get(m.Industry), Get(m.Country), Get(m.CompanySize));
    }

    /// <summary>Returns problems with the mapping itself (missing email, unknown headers, same header mapped twice).</summary>
    public static IReadOnlyList<string> Validate(ColumnMapping m, IReadOnlyCollection<string> headers)
    {
        var problems = new List<string>();
        var known = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(m.Email)) problems.Add("An email column is required.");

        var mapped = new (string Field, string? Header)[]
        {
            ("Name", m.Name), ("FirstName", m.FirstName), ("LastName", m.LastName), ("Email", m.Email), ("Title", m.Title),
            ("CompanyName", m.CompanyName), ("CompanyDomain", m.CompanyDomain), ("Industry", m.Industry),
            ("Country", m.Country), ("CompanySize", m.CompanySize)
        }.Where(x => !string.IsNullOrWhiteSpace(x.Header)).ToList();

        foreach (var (field, header) in mapped.Where(x => !known.Contains(x.Header!)))
            problems.Add($"{field}: column \"{header}\" is not in the file.");

        foreach (var g in mapped.GroupBy(x => x.Header!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Column \"{g.Key}\" is mapped to more than one field ({string.Join(", ", g.Select(x => x.Field))}).");

        if (!string.IsNullOrWhiteSpace(m.Name) && (!string.IsNullOrWhiteSpace(m.FirstName) || !string.IsNullOrWhiteSpace(m.LastName)))
            problems.Add("Map either Name or First name / Last name, not both.");

        return problems;
    }

    /// <summary>Rewrites each mapped header to the file's exact spelling (case-insensitive match); unknown headers are left as given for <see cref="Validate"/> to report.</summary>
    public static ColumnMapping Normalize(ColumnMapping m, IReadOnlyCollection<string> headers)
    {
        string? Fix(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return null;
            var trimmed = h.Trim();
            return headers.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
        }
        return new ColumnMapping(Fix(m.Name), Fix(m.FirstName), Fix(m.LastName), Fix(m.Email) ?? "", Fix(m.Title),
            Fix(m.CompanyName), Fix(m.CompanyDomain), Fix(m.Industry), Fix(m.Country), Fix(m.CompanySize));
    }
}
