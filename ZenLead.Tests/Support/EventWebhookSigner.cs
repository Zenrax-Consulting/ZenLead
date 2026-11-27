using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZenLead.Tests.Support;

/// <summary>
/// Plays SendGrid's side of the signed Event Webhook with a P-256 key generated per test run (never a real verification key):
/// signs UTF-8(timestamp) + raw body bytes, DER-encoded, and exposes the public key as base64 SPKI like the SendGrid UI does.
/// </summary>
public sealed class EventWebhookSigner : IDisposable
{
    public static readonly EventWebhookSigner Shared = new();

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string Sign(string timestamp, byte[] body)
    {
        var payload = Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray();
        return Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Handcrafted Event Webhook payloads in SendGrid's documented shape (snake_case, custom args top-level, extra fields we ignore).</summary>
public static class EventFixtures
{
    public static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static Dictionary<string, object?> Event(string type, Guid emailMessageId, Guid workspaceId, DateTime at, string? sgEventId = null,
        string email = "lead@example.com", string? bounceType = null, string? reason = null, string? status = null, string? smtpId = null)
    {
        var e = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["timestamp"] = Unix(at),
            ["event"] = type,
            ["sg_event_id"] = sgEventId ?? $"evt-{Guid.NewGuid():N}",
            ["sg_message_id"] = "14c5d75ce93.dfd.64b469.filter0001.16648.5515E0B88.0",
            ["smtp-id"] = smtpId ?? "<14c5d75ce93.dfd.64b469@ismtpd-555>",
            ["category"] = new[] { "campaign" },
            ["emailMessageId"] = emailMessageId.ToString(),
            ["workspaceId"] = workspaceId.ToString(),
            ["campaignId"] = Guid.NewGuid().ToString()
        };
        if (bounceType is not null) e["type"] = bounceType;
        if (reason is not null) e["reason"] = reason;
        if (status is not null) e["status"] = status;
        if (type == "open")
        {
            e["useragent"] = "Mozilla/4.0 (compatible; MSIE 6.1; Windows XP; .NET CLR 1.1.4322; .NET CLR 2.0.50727)";
            e["ip"] = "255.255.255.255";
            e["sg_machine_open"] = false;
        }
        if (type == "delivered")
        {
            e["response"] = "250 OK";
            e["tls"] = 1;
            e["pool"] = new { name = "new_MY_test", id = 210 };
        }
        return e;
    }

    public static byte[] Body(params Dictionary<string, object?>[] events) => JsonSerializer.SerializeToUtf8Bytes(events);
}
