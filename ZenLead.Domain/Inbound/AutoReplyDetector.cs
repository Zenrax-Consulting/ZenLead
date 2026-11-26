namespace ZenLead.Domain.Inbound;

public enum AutoReplyKind { None, OutOfOffice, Bounce }

/// <summary>
/// Recognises machine-generated replies from headers (RFC 3834 and common vendor variants), the sender and, as the weakest
/// signal, the subject. Never looks at the body: a human writing "I was out of office" is a real reply.
/// </summary>
public static class AutoReplyDetector
{
    private static readonly string[] OooSubjectPrefixes = ["Automatic reply:", "Out of Office", "Autoreply:", "Auto:"];
    private static readonly string[] BounceLocalParts = ["mailer-daemon", "postmaster"];

    public static AutoReplyKind Detect(IReadOnlyDictionary<string, string> headers, string? fromAddress, string? subject)
    {
        string? H(string name) => TryGet(headers, name);

        // bounces first: a DSN often carries Auto-Submitted as well
        var contentType = H("Content-Type");
        if (contentType is not null && contentType.Contains("multipart/report", StringComparison.OrdinalIgnoreCase)
            && contentType.Contains("delivery-status", StringComparison.OrdinalIgnoreCase))
            return AutoReplyKind.Bounce;
        if (H("Return-Path") is { } returnPath && returnPath.Trim() == "<>") return AutoReplyKind.Bounce;
        if (LocalPart(fromAddress) is { } local && BounceLocalParts.Contains(local, StringComparer.OrdinalIgnoreCase)) return AutoReplyKind.Bounce;

        if (H("Auto-Submitted") is { } autoSubmitted && !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            return AutoReplyKind.OutOfOffice;
        if (H("X-Autoreply") is not null || H("X-Autorespond") is not null || H("X-Auto-Response-Suppress") is not null)
            return AutoReplyKind.OutOfOffice;
        if (H("Precedence") is { } precedence && precedence.Trim().ToLowerInvariant() is "auto_reply" or "bulk" or "junk")
            return AutoReplyKind.OutOfOffice;

        // lowest-confidence signal, only when no header matched
        var s = subject?.TrimStart() ?? "";
        return OooSubjectPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? AutoReplyKind.OutOfOffice : AutoReplyKind.None;
    }

    private static string? TryGet(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var v)) return v;
        foreach (var (key, value) in headers)                                      // tolerate a case-sensitive dictionary
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static string? LocalPart(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var at = address.IndexOf('@');
        return (at < 0 ? address : address[..at]).Trim();
    }
}
