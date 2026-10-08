namespace ZenLead.Domain.Discovery;

/// <param name="Max">Null means open-ended ("1000+").</param>
public readonly record struct CompanySizeRange(int Min, int? Max);

public static class CompanySizeParser
{
    /// <summary>"51-200" → (51,200); "1000+" → (1000,null); "500" → (500,500); anything else → null.</summary>
    public static CompanySizeRange? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().Replace(",", "");

        if (v.EndsWith('+'))
            return int.TryParse(v[..^1].Trim(), out var open) && open >= 0 ? new CompanySizeRange(open, null) : null;

        var dash = v.IndexOf('-');
        if (dash > 0)
        {
            if (!int.TryParse(v[..dash].Trim(), out var lo) || !int.TryParse(v[(dash + 1)..].Trim(), out var hi)) return null;
            return lo >= 0 && hi >= lo ? new CompanySizeRange(lo, hi) : null;
        }

        return int.TryParse(v, out var exact) && exact >= 0 ? new CompanySizeRange(exact, exact) : null;
    }
}
