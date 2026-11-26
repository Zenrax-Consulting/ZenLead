using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Security;

/// <summary>Bound from the "Unsubscribe" section. <c>SigningKey</c> is a required secret (≥ 32 bytes); rotating it breaks every old link.</summary>
public class UnsubscribeOptions
{
    public const string SectionName = "Unsubscribe";
    public string SigningKey { get; set; } = string.Empty;
}

/// <summary>
/// <c>base64url("{workspaceId:N}.{leadId:N}") + "." + base64url(HMAC-SHA256(payload, key))</c>. Tokens never expire: an unsubscribe
/// link in an old email must keep working.
/// </summary>
public sealed class HmacUnsubscribeTokens : IUnsubscribeTokens
{
    public const int MinKeyBytes = 32;
    private const int MaxTokenLength = 256;           // a valid token is ~130 chars; anything far longer is junk

    private readonly byte[] _key;

    public HmacUnsubscribeTokens(IOptions<UnsubscribeOptions> options)
    {
        _key = Encoding.UTF8.GetBytes(options.Value.SigningKey ?? string.Empty);
        if (_key.Length < MinKeyBytes)
            throw new InvalidOperationException($"'Unsubscribe:SigningKey' must be at least {MinKeyBytes} bytes long.");
    }

    public string Create(Guid workspaceId, Guid leadId)
    {
        var payload = Encoding.ASCII.GetBytes($"{workspaceId:N}.{leadId:N}");
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(HMACSHA256.HashData(_key, payload))}";
    }

    public bool TryParse(string token, out Guid workspaceId, out Guid leadId)
    {
        workspaceId = Guid.Empty;
        leadId = Guid.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.') || dot == token.Length - 1) return false;

        byte[] payload, mac;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), mac)) return false;

        var parts = Encoding.ASCII.GetString(payload).Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var ws) || !Guid.TryParseExact(parts[1], "N", out var lead)) return false;

        workspaceId = ws;
        leadId = lead;
        return true;
    }
}
