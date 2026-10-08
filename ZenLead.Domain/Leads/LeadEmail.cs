using System.Net.Mail;

namespace ZenLead.Domain.Leads;

public static class LeadEmail
{
    public const int MaxLength = 256;

    /// <summary>Trim + lowercase. Returns null when the value is not a plausible single address.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().ToLowerInvariant();
        if (value.Length > MaxLength || value.Contains(' ') || value.Contains(',') || value.Contains(';')) return null;
        if (!MailAddress.TryCreate(value, out var parsed) || parsed.Address != value) return null; // rejects "Name <a@b.c>"
        var at = value.IndexOf('@');
        return at > 0 && value.IndexOf('.', at) > at + 1 ? value : null; // needs a dot in the domain part
    }
}
