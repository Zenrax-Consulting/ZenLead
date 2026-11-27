namespace ZenLead.Application.Abstractions;

/// <summary>Authenticates a SendGrid Event Webhook post (F28). Malformed input is just "invalid": it never throws.</summary>
public interface IEventSignatureVerifier
{
    /// <param name="rawBody">The exact request bytes: the signature covers these, not a re-serialised form.</param>
    /// <param name="timestamp">The <c>X-Twilio-Email-Event-Webhook-Timestamp</c> header (unix seconds).</param>
    /// <param name="signature">The <c>X-Twilio-Email-Event-Webhook-Signature</c> header (base64 DER ECDSA).</param>
    bool Verify(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature);
}
