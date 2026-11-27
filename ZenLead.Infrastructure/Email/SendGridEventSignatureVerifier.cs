using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Webhooks;

namespace ZenLead.Infrastructure.Email;

/// <summary>
/// SendGrid "Signed Event Webhook Requests": the signature header is a base64 DER ECDSA (P-256, SHA-256) signature over the UTF-8
/// timestamp header followed by the raw body bytes; the verification key from the SendGrid UI is base64 SubjectPublicKeyInfo.
/// The timestamp window is wide on purpose (SendGrid retries for 24 h with the original signature); replays are neutralised by
/// <c>sg_event_id</c> idempotency, not by the clock.
/// </summary>
public class SendGridEventSignatureVerifier(IOptions<EventWebhookOptions> options, TimeProvider clock) : IEventSignatureVerifier
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public bool Verify(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(o.PublicKey)) return false;
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        if (seconds is < 0 or > 253_402_300_799) return false;

        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age > TimeSpan.FromHours(o.MaxAgeHours) || age < -MaxClockSkew) return false;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(o.PublicKey.Trim()), out _);
            var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
            var payload = new byte[timestampBytes.Length + rawBody.Length];
            timestampBytes.CopyTo(payload, 0);
            rawBody.CopyTo(payload.AsSpan(timestampBytes.Length));
            return ecdsa.VerifyData(payload, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;                                                       // malformed key/signature is just "invalid", never a 500
        }
    }

    /// <summary>For startup validation: can this value be imported as an EC public key?</summary>
    public static bool IsValidPublicKey(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
