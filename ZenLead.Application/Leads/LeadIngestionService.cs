using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Application.Leads;

/// <summary>
/// The only way leads enter the system besides single manual create (which also uses it).
/// Callers pass already-parsed rows in batches (CSV: ~500, discovery: one provider page); this class never reads files or calls providers.
/// </summary>
public class LeadIngestionService(ILeadIngestionStore store)
{
    public async Task<IngestionSummary> IngestAsync(
        Guid workspaceId, IReadOnlyList<CandidateLead> rows, IngestionSource source, CancellationToken ct = default)
    {
        var results = new IngestionRowResult?[rows.Count];
        var firstSeen = new Dictionary<string, int>();           // email → index of first valid occurrence in this batch
        var candidates = new List<(int Index, string Email, CandidateLead Row)>();

        for (var i = 0; i < rows.Count; i++)
        {
            var email = LeadEmail.Normalize(rows[i].Email);
            if (email is null) { results[i] = new(i, IngestionOutcome.Invalid, "Invalid email address"); continue; }
            if (rows[i].Verification == EmailVerificationStatus.Invalid) { results[i] = new(i, IngestionOutcome.Invalid, "Provider marked email invalid"); continue; }
            if (!firstSeen.TryAdd(email, i)) { results[i] = new(i, IngestionOutcome.Duplicate, "Duplicate within this batch"); continue; }
            candidates.Add((i, email, rows[i]));
        }

        var existing = candidates.Count == 0
            ? new Dictionary<string, ExistingLead>()
            : (await store.FindExistingAsync(workspaceId, candidates.Select(c => c.Email).ToList(), ct)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var toInsert = new List<(int Index, string Email, CandidateLead Row)>();
        foreach (var c in candidates)
        {
            if (!existing.TryGetValue(c.Email, out var found)) { toInsert.Add(c); continue; }
            results[c.Index] = LeadStatusRules.IsSuppressed(found.Status)
                ? new(c.Index, IngestionOutcome.Suppressed, $"Lead is {found.Status}", found.Id)
                : new(c.Index, IngestionOutcome.Duplicate, found.IsDeleted ? "Previously deleted lead" : "Already exists", found.Id);
        }

        if (toInsert.Count > 0)
        {
            var companyRequests = toInsert
                .Select(t => (Key: CompanyKey.From(t.Row.CompanyName, t.Row.CompanyDomain), t.Row))
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!)
                .Select(g => (g.Key, Industry: g.Select(x => x.Row.Industry).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                              Country: g.Select(x => x.Row.Country).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                              Size: g.Select(x => x.Row.CompanySize).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))))
                .ToList();
            var companyIds = companyRequests.Count == 0
                ? new Dictionary<CompanyKey, Guid>()
                : (await store.UpsertCompaniesAsync(workspaceId, companyRequests, ct)).ToDictionary(kv => kv.Key, kv => kv.Value);

            var now = DateTime.UtcNow;
            var leads = toInsert.Select(t =>
            {
                var key = CompanyKey.From(t.Row.CompanyName, t.Row.CompanyDomain);
                return new Lead
                {
                    Id = Guid.NewGuid(), WorkspaceId = workspaceId,
                    Name = Truncate(string.IsNullOrWhiteSpace(t.Row.Name) ? t.Email.Split('@')[0] : t.Row.Name.Trim(), 200)!,
                    Email = t.Email, Title = Truncate(t.Row.Title?.Trim(), 200),
                    Status = LeadStatus.New, CreatedAt = now,
                    CompanyId = key is not null && companyIds.TryGetValue(key, out var cid) ? cid : null,
                    Source = source.Source, SourceRunId = source.SourceRunId,
                    EmailVerificationStatus = t.Row.Verification
                };
            }).ToList();

            var landed = await store.InsertLeadsAsync(leads, ct);
            for (var k = 0; k < toInsert.Count; k++)
                results[toInsert[k].Index] = landed.Contains(leads[k].Id)
                    ? new(toInsert[k].Index, IngestionOutcome.Imported, null, leads[k].Id)
                    : new(toInsert[k].Index, IngestionOutcome.Duplicate, "Created concurrently by another import");
        }

        return new IngestionSummary(results!);
    }

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}
