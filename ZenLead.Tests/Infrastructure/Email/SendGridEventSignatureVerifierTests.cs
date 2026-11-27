using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZenLead.Application.Webhooks;
using ZenLead.Infrastructure.Email;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Email;

/// <summary>F28: the signed Event Webhook. Keys are generated per run; nothing here talks to SendGrid.</summary>
public sealed class SendGridEventSignatureVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly EventWebhookSigner _signer = new();
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly byte[] _body = Encoding.UTF8.GetBytes("""[{"email":"a@example.com","timestamp":1764244800,"event":"delivered","sg_event_id":"e1"}]""");

    private SendGridEventSignatureVerifier Verifier(string? publicKey = null, int maxAgeHours = 26)
        => new(Options.Create(new EventWebhookOptions { PublicKey = publicKey ?? _signer.PublicKey, MaxAgeHours = maxAgeHours }), _clock);

    private static string Ts(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString();

    public void Dispose() => _signer.Dispose();

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var ts = Ts(Now);
        Assert.True(Verifier().Verify(_body, ts, _signer.Sign(ts, _body)));
    }

    [Fact]
    public void One_flipped_byte_in_the_body_is_rejected()
    {
        var ts = Ts(Now);
        var sig = _signer.Sign(ts, _body);
        var tampered = (byte[])_body.Clone();
        tampered[10] ^= 0x01;
        Assert.False(Verifier().Verify(tampered, ts, sig));
    }

    [Fact]
    public void Wrong_timestamp_is_rejected()
    {
        var sig = _signer.Sign(Ts(Now), _body);
        Assert.False(Verifier().Verify(_body, Ts(Now.AddSeconds(-1)), sig));
    }

    [Fact]
    public void Signature_from_a_different_key_is_rejected()
    {
        using var other = new EventWebhookSigner();
        var ts = Ts(Now);
        Assert.False(Verifier().Verify(_body, ts, other.Sign(ts, _body)));
    }

    [Theory]
    [InlineData(null, "sig")]
    [InlineData("", "sig")]
    [InlineData("1764244800", null)]
    [InlineData("1764244800", "")]
    public void Missing_or_empty_headers_are_rejected(string? ts, string? sig)
        => Assert.False(Verifier().Verify(_body, ts, sig));

    [Fact]
    public void Missing_public_key_rejects_everything()
    {
        var ts = Ts(Now);
        var verifier = new SendGridEventSignatureVerifier(Options.Create(new EventWebhookOptions { PublicKey = null }), _clock);
        Assert.False(verifier.Verify(_body, ts, _signer.Sign(ts, _body)));
    }

    [Fact]
    public void Malformed_input_is_invalid_and_never_throws()
    {
        var ts = Ts(Now);
        var sig = _signer.Sign(ts, _body);
        Assert.False(Verifier().Verify(_body, "not-a-number", sig));
        Assert.False(Verifier().Verify(_body, "-5", sig));
        Assert.False(Verifier().Verify(_body, ts, "!!! not base64 !!!"));
        Assert.False(Verifier().Verify(_body, ts, Convert.ToBase64String([1, 2, 3])));
        Assert.False(Verifier(publicKey: "garbage key").Verify(_body, ts, sig));
        Assert.False(Verifier(publicKey: Convert.ToBase64String([1, 2, 3, 4])).Verify(_body, ts, sig));
    }

    [Fact]
    public void Too_old_or_future_timestamps_are_rejected_but_a_one_hour_old_retry_is_accepted()
    {
        string Signed(DateTimeOffset at, out string ts)
        {
            ts = Ts(at);
            return _signer.Sign(ts, _body);
        }

        var sig = Signed(Now.AddHours(-27), out var ts);
        Assert.False(Verifier().Verify(_body, ts, sig));                      // older than MaxAgeHours (26)

        sig = Signed(Now.AddMinutes(6), out ts);
        Assert.False(Verifier().Verify(_body, ts, sig));                      // > 5 min in the future

        sig = Signed(Now.AddHours(-1), out ts);
        Assert.True(Verifier().Verify(_body, ts, sig));                       // a legitimate SendGrid retry

        sig = Signed(Now.AddMinutes(4), out ts);
        Assert.True(Verifier().Verify(_body, ts, sig));                       // small clock skew is fine
    }

    [Fact]
    public void Re_serialised_json_with_the_same_data_is_rejected()
    {
        var ts = Ts(Now);
        var sig = _signer.Sign(ts, _body);
        var reserialised = JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<JsonElement>(_body), new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(_body, reserialised);
        Assert.False(Verifier().Verify(reserialised, ts, sig));               // proves verification runs on the raw bytes
    }

    [Fact]
    public void Public_key_validation_for_startup()
    {
        Assert.True(SendGridEventSignatureVerifier.IsValidPublicKey(_signer.PublicKey));
        Assert.False(SendGridEventSignatureVerifier.IsValidPublicKey(null));
        Assert.False(SendGridEventSignatureVerifier.IsValidPublicKey("abc"));
        using var rsa = RSA.Create(2048);
        Assert.False(SendGridEventSignatureVerifier.IsValidPublicKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
    }
}
