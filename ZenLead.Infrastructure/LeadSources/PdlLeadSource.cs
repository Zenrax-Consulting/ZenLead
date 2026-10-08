using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Discovery;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.LeadSources;

/// <summary>
/// People Data Labs Person Search (POST /v5/person/search, X-Api-Key header, scroll_token pagination).
/// PDL charges one credit per matched record returned, so CreditsUsed = records on the page.
/// Re-read PDL's terms on storing returned data before the first real run.
/// </summary>
public class PdlLeadSource(HttpClient http) : ILeadSource
{
    public const string ProviderName = "Pdl";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // PDL reports company size as fixed buckets; a numeric range is translated to the buckets that overlap it.
    private static readonly (string Bucket, int Min, int? Max)[] SizeBuckets =
    [
        ("1-10", 1, 10), ("11-50", 11, 50), ("51-200", 51, 200), ("201-500", 201, 500),
        ("501-1000", 501, 1000), ("1001-5000", 1001, 5000), ("5001-10000", 5001, 10000), ("10001+", 10001, null)
    ];

    public string Name => ProviderName;

    public async Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["query"] = BuildQuery(criteria), ["size"] = Math.Clamp(limit, 1, 100) };
        if (cursor is not null) body["scroll_token"] = cursor;

        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync("v5/person/search", body, Json, ct); }
        catch (HttpRequestException ex) { throw new LeadSourceException(LeadSourceFailureKind.Unavailable, "PDL request failed", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LeadSourceException(LeadSourceFailureKind.Unavailable, "PDL request timed out", ex); }

        using (response)
        {
            // PDL answers 404 when a search simply has no matches: an empty page, not a failure
            if (response.StatusCode == HttpStatusCode.NotFound) return new LeadSearchPage([], null, 0);
            if (!response.IsSuccessStatusCode) throw MapFailure(response.StatusCode);

            PdlSearchResponse? parsed;
            try { parsed = await response.Content.ReadFromJsonAsync<PdlSearchResponse>(Json, ct); }
            catch (JsonException ex) { throw new LeadSourceException(LeadSourceFailureKind.InvalidResponse, "PDL returned an unparseable body", ex); }
            if (parsed?.Data is null) throw new LeadSourceException(LeadSourceFailureKind.InvalidResponse, "PDL response had no data array");

            var leads = parsed.Data.Where(p => !string.IsNullOrWhiteSpace(p.Id)).Select(Map).ToList();
            var next = string.IsNullOrWhiteSpace(parsed.ScrollToken) || leads.Count == 0 ? null : parsed.ScrollToken;
            return new LeadSearchPage(leads, next, leads.Count);
        }
    }

    public Task<int?> GetRemainingCreditsAsync(CancellationToken ct) => Task.FromResult<int?>(null);   // account usage needs a separate PDL endpoint/tier

    internal static object BuildQuery(LeadSearchCriteria c)
    {
        var must = new List<object>();
        if (c.JobTitles.Count > 0) must.Add(Should(c.JobTitles.Select(t => new { match = new Dictionary<string, string> { ["job_title"] = t.ToLowerInvariant() } })));
        if (c.Industries.Count > 0) must.Add(new { terms = new Dictionary<string, string[]> { ["job_company_industry"] = c.Industries.Select(x => x.ToLowerInvariant()).ToArray() } });
        if (c.Countries.Count > 0) must.Add(new { terms = new Dictionary<string, string[]> { ["location_country"] = c.Countries.Select(x => x.ToLowerInvariant()).ToArray() } });
        if (c.CompanyDomains.Count > 0) must.Add(new { terms = new Dictionary<string, string[]> { ["job_company_website"] = c.CompanyDomains.Select(x => x.ToLowerInvariant()).ToArray() } });
        if (c.CompanySizeMin is not null || c.CompanySizeMax is not null)
        {
            var buckets = SizeBuckets
                .Where(b => (c.CompanySizeMin is not { } min || (b.Max ?? int.MaxValue) >= min) && (c.CompanySizeMax is not { } max || b.Min <= max))
                .Select(b => b.Bucket).ToArray();
            must.Add(new { terms = new Dictionary<string, string[]> { ["job_company_size"] = buckets } });
        }
        must.Add(new { exists = new { field = "work_email" } });    // never pay for rows we cannot email
        return new { @bool = new { must } };
    }

    private static object Should(IEnumerable<object> clauses) => new { @bool = new { should = clauses.ToList(), minimum_should_match = 1 } };

    private static DiscoveredLead Map(PdlPerson p) => new(
        p.Id!, p.FirstName, p.LastName, p.WorkEmail, p.JobTitle, p.JobCompanyName, p.JobCompanyWebsite,
        p.JobCompanyIndustry, p.LocationCountry, p.JobCompanySize, MapVerification(p));

    // "verified"→Verified, "guessed"/"likely"→Risky, "invalid"→Invalid, absent→Unverified
    internal static EmailVerificationStatus MapVerification(PdlPerson p) => p.EmailStatus?.ToLowerInvariant() switch
    {
        "verified" => EmailVerificationStatus.Verified,
        "guessed" or "likely" => EmailVerificationStatus.Risky,
        "invalid" => EmailVerificationStatus.Invalid,
        _ => EmailVerificationStatus.Unverified
    };

    private static LeadSourceException MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(LeadSourceFailureKind.Unauthorized, $"PDL rejected credentials ({(int)status})"),
        HttpStatusCode.PaymentRequired => new(LeadSourceFailureKind.OutOfCredits, "PDL account is out of credits"),
        HttpStatusCode.TooManyRequests => new(LeadSourceFailureKind.RateLimited, "PDL rate limit"),
        >= HttpStatusCode.InternalServerError => new(LeadSourceFailureKind.Unavailable, $"PDL server error ({(int)status})"),
        _ => new(LeadSourceFailureKind.InvalidResponse, $"PDL returned unexpected status {(int)status}")
    };

    internal class PdlSearchResponse
    {
        public List<PdlPerson>? Data { get; set; }
        [JsonPropertyName("scroll_token")] public string? ScrollToken { get; set; }
    }

    internal class PdlPerson
    {
        public string? Id { get; set; }
        [JsonPropertyName("first_name")] public string? FirstName { get; set; }
        [JsonPropertyName("last_name")] public string? LastName { get; set; }
        [JsonPropertyName("work_email")] public string? WorkEmail { get; set; }
        [JsonPropertyName("email_status")] public string? EmailStatus { get; set; }       // optional; absent on most tiers
        [JsonPropertyName("job_title")] public string? JobTitle { get; set; }
        [JsonPropertyName("job_company_name")] public string? JobCompanyName { get; set; }
        [JsonPropertyName("job_company_website")] public string? JobCompanyWebsite { get; set; }
        [JsonPropertyName("job_company_industry")] public string? JobCompanyIndustry { get; set; }
        [JsonPropertyName("job_company_size")] public string? JobCompanySize { get; set; }
        [JsonPropertyName("location_country")] public string? LocationCountry { get; set; }
    }
}
