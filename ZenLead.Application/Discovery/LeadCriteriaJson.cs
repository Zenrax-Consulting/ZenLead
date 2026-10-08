using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Discovery;

namespace ZenLead.Application.Discovery;

/// <summary>Storage format and DTO mapping for <see cref="LeadSearchCriteria"/>.</summary>
public static class LeadCriteriaJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(LeadSearchCriteria criteria) => JsonSerializer.Serialize(criteria, Options);

    /// <summary>Malformed or missing JSON falls back to <see cref="LeadSearchCriteria.Empty"/>.</summary>
    public static LeadSearchCriteria Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return LeadSearchCriteria.Empty;
        try
        {
            var c = JsonSerializer.Deserialize<LeadSearchCriteria>(json, Options);
            return c is null ? LeadSearchCriteria.Empty : Normalize(c);
        }
        catch (JsonException) { return LeadSearchCriteria.Empty; }
    }

    public static LeadSearchCriteria ToCriteria(CriteriaDto dto) => Normalize(new LeadSearchCriteria(
        dto.JobTitles, dto.Industries, dto.Countries, dto.CompanySizeMin, dto.CompanySizeMax, dto.CompanyDomains));

    public static CriteriaDto ToDto(LeadSearchCriteria c)
        => new(c.JobTitles, c.Industries, c.Countries, c.CompanySizeMin, c.CompanySizeMax, c.CompanyDomains);

    /// <summary>Trim, drop blanks, de-duplicate (case-insensitive), normalise domains; null lists (missing JSON properties) become empty.</summary>
    private static LeadSearchCriteria Normalize(LeadSearchCriteria c) => new(
        Clean(c.JobTitles), Clean(c.Industries), Clean(c.Countries), c.CompanySizeMin, c.CompanySizeMax,
        Clean((c.CompanyDomains ?? []).Select(CompanyKey.NormalizeDomain)));

    private static IReadOnlyList<string> Clean(IEnumerable<string?>? values)
        => (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
