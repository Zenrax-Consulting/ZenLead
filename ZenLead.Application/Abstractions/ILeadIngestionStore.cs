using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

public record ExistingLead(Guid Id, LeadStatus Status, bool IsDeleted);

/// <summary>Identity of a company within a workspace: domain when we have one, otherwise case-insensitive name.</summary>
public record CompanyKey(string? Domain, string Name)
{
    public static CompanyKey? From(string? name, string? domain)
    {
        var d = NormalizeDomain(domain);
        var n = name?.Trim();
        if (d is null && string.IsNullOrEmpty(n)) return null;
        return new CompanyKey(d, string.IsNullOrEmpty(n) ? d! : n);
    }

    public static string? NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        v = v.Replace("https://", "").Replace("http://", "");
        if (v.StartsWith("www.")) v = v[4..];
        v = v.Split('/', '?', '#')[0];
        return v.Contains('.') ? v : null;
    }
}

public interface ILeadIngestionStore
{
    /// <summary>One batched query (<c>Email IN (...)</c>), including soft-deleted rows. Keys are lowercase emails.</summary>
    Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct);

    /// <summary>Find-or-create companies; returns the id for every requested key.</summary>
    Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct);

    /// <summary>
    /// Inserts in one SaveChanges. If the unique (WorkspaceId, Email) index rejects the batch (a concurrent import won the race),
    /// falls back to row-by-row and returns only the ids that actually landed.
    /// </summary>
    Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct);
}
