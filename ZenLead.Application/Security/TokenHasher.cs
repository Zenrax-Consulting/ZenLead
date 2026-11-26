using System.Security.Cryptography;
using System.Text;

namespace ZenLead.Application.Security;

/// <summary>How every bearer-style token (refresh, approval, password reset) is stored: Base64 SHA-256 of the raw value (44 chars).</summary>
public static class TokenHasher
{
    public static string Hash(string raw) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>256-bit random, URL-safe base64 without padding (same output as <c>WebEncoders.Base64UrlEncode</c>).</summary>
    public static string NewUrlSafeToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
