namespace ZenLead.Domain.Scheduling;

/// <summary>
/// The Reply-To codec: <c>r-{enrollmentId:N}@{inboundDomain}</c>. F25 decodes it to route an inbound reply to its enrollment.
/// A GUID is 122 random bits: unguessable, so no MAC is needed in the address (F25 still checks the sender matches the lead).
/// </summary>
public static class ReplyAddress
{
    private const string Prefix = "r-";

    public static string Encode(Guid enrollmentId, string inboundDomain) => $"{Prefix}{enrollmentId:N}@{inboundDomain}";

    public static bool TryDecode(string? address, string inboundDomain, out Guid enrollmentId)
    {
        enrollmentId = Guid.Empty;
        if (address is null) return false;
        var at = address.LastIndexOf('@');
        if (at <= 0 || !address[(at + 1)..].Equals(inboundDomain, StringComparison.OrdinalIgnoreCase)) return false;
        var local = address[..at];
        return local.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(local[Prefix.Length..], "N", out enrollmentId);
    }
}
