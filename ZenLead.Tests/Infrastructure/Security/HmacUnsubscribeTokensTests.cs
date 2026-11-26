using Microsoft.Extensions.Options;
using ZenLead.Infrastructure.Security;

namespace ZenLead.Tests.Infrastructure.Security;

public class HmacUnsubscribeTokensTests
{
    private static HmacUnsubscribeTokens Tokens(string key = "0123456789abcdef0123456789abcdef-test")
        => new(Options.Create(new UnsubscribeOptions { SigningKey = key }));

    [Fact]
    public void Round_Trips()
    {
        var (ws, lead) = (Guid.NewGuid(), Guid.NewGuid());
        var token = Tokens().Create(ws, lead);

        Assert.True(Tokens().TryParse(token, out var parsedWs, out var parsedLead));
        Assert.Equal(ws, parsedWs);
        Assert.Equal(lead, parsedLead);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);       // URL-safe as is
    }

    [Fact]
    public void Tampered_Payload_Is_Rejected()
    {
        var tokens = Tokens();
        var genuine = tokens.Create(Guid.NewGuid(), Guid.NewGuid());
        var otherPayload = tokens.Create(Guid.NewGuid(), Guid.NewGuid()).Split('.')[0];
        var forged = otherPayload + "." + genuine.Split('.')[1];

        Assert.False(tokens.TryParse(forged, out var ws, out var lead));
        Assert.Equal(Guid.Empty, ws);
        Assert.Equal(Guid.Empty, lead);
    }

    [Fact]
    public void Tampered_Mac_Is_Rejected()
    {
        var token = Tokens().Create(Guid.NewGuid(), Guid.NewGuid());
        var last = token[^1];
        var tampered = token[..^1] + (last == 'A' ? 'B' : 'A');

        Assert.False(Tokens().TryParse(tampered, out _, out _));
    }

    [Fact]
    public void Wrong_Key_Is_Rejected()
    {
        var token = Tokens().Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(Tokens("another-key-that-is-also-32-bytes-long!!").TryParse(token, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("abc")]
    [InlineData("abc.")]
    [InlineData(".abc")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    [InlineData("Zm9v.YmFy")]
    [InlineData("\0\0.\0")]
    public void Malformed_Strings_Never_Throw(string token)
        => Assert.False(Tokens().TryParse(token, out _, out _));

    [Fact]
    public void Very_Long_Input_Is_Rejected()
        => Assert.False(Tokens().TryParse(new string('a', 10_000) + "." + new string('b', 10_000), out _, out _));

    [Fact]
    public void Short_Key_Is_Refused()
        => Assert.Throws<InvalidOperationException>(() => Tokens("short"));
}
