using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Infrastructure.Email;

namespace ZenLead.Tests.Infrastructure.Email;

public class SendGridPayloadMapperTests
{
    private static readonly EmailOptions Options = new() { FromAddress = "info@zenrax.test", FromName = "Zenrax" };

    private static OutboundEmail Basic(string? html = "<p>Hi</p>") =>
        new(new EmailAddress("ann@acme.test", "Ann"), "Hello", "Hi", html);

    // assert on the JSON SendGrid actually receives, not on SDK internals
    private static JsonElement Json(OutboundEmail email) =>
        JsonDocument.Parse(SendGridPayloadMapper.ToMessage(email, Options).Serialize()).RootElement;

    [Fact]
    public void Maps_to_subject_and_both_bodies()
    {
        var json = Json(Basic());

        var to = json.GetProperty("personalizations")[0].GetProperty("to")[0];
        Assert.Equal("ann@acme.test", to.GetProperty("email").GetString());
        Assert.Equal("Ann", to.GetProperty("name").GetString());
        Assert.Equal("Hello", json.GetProperty("subject").GetString());
        var content = json.GetProperty("content").EnumerateArray().ToDictionary(c => c.GetProperty("type").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("Hi", content["text/plain"]);
        Assert.Equal("<p>Hi</p>", content["text/html"]);
    }

    [Fact]
    public void Html_is_omitted_when_null()
    {
        var types = Json(Basic(html: null)).GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();

        Assert.Equal(["text/plain"], types);
    }

    [Fact]
    public void From_defaults_to_configured_sender()
    {
        var from = Json(Basic()).GetProperty("from");

        Assert.Equal("info@zenrax.test", from.GetProperty("email").GetString());
        Assert.Equal("Zenrax", from.GetProperty("name").GetString());
    }

    [Fact]
    public void From_override_is_honoured()
    {
        var from = Json(Basic() with { From = new EmailAddress("sam@zenrax.test", "Sam") }).GetProperty("from");

        Assert.Equal("sam@zenrax.test", from.GetProperty("email").GetString());
        Assert.Equal("Sam", from.GetProperty("name").GetString());
    }

    [Fact]
    public void ReplyTo_headers_custom_args_and_categories_are_present()
    {
        var json = Json(Basic() with
        {
            ReplyTo = new EmailAddress("r+abc@reply.zenrax.test"),
            Headers = new Dictionary<string, string> { ["In-Reply-To"] = "<x@y>", ["List-Unsubscribe"] = "<https://u.test/1>" },
            CustomArgs = new Dictionary<string, string> { ["emailMessageId"] = "42", ["workspaceId"] = "w1" },
            Categories = ["campaign", "step-1"]
        });

        Assert.Equal("r+abc@reply.zenrax.test", json.GetProperty("reply_to").GetProperty("email").GetString());
        var p = json.GetProperty("personalizations")[0];
        Assert.Equal("<x@y>", p.GetProperty("headers").GetProperty("In-Reply-To").GetString());
        Assert.Equal("<https://u.test/1>", p.GetProperty("headers").GetProperty("List-Unsubscribe").GetString());
        Assert.Equal("42", p.GetProperty("custom_args").GetProperty("emailMessageId").GetString());
        Assert.Equal("w1", p.GetProperty("custom_args").GetProperty("workspaceId").GetString());
        Assert.Equal(["campaign", "step-1"], json.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Click_and_subscription_tracking_are_off_and_open_tracking_follows_TrackOpens(bool trackOpens)
    {
        var tracking = Json(Basic() with { TrackOpens = trackOpens }).GetProperty("tracking_settings");

        Assert.False(tracking.GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.False(tracking.GetProperty("click_tracking").GetProperty("enable_text").GetBoolean());
        Assert.False(tracking.GetProperty("subscription_tracking").GetProperty("enable").GetBoolean());
        Assert.Equal(trackOpens, tracking.GetProperty("open_tracking").GetProperty("enable").GetBoolean());
    }

    [Theory]
    [InlineData("X-Custom", "a\r\nBcc: evil@x.test")]
    [InlineData("X-Custom", "a\nb")]
    [InlineData("X-Bad\r\nBcc", "v")]
    public void Line_breaks_in_headers_are_rejected(string key, string value)
    {
        var email = Basic() with { Headers = new Dictionary<string, string> { [key] = value } };

        Assert.Throws<ArgumentException>(() => SendGridPayloadMapper.ToMessage(email, Options));
    }

    [Fact]
    public void Line_breaks_in_custom_args_are_rejected()
    {
        var email = Basic() with { CustomArgs = new Dictionary<string, string> { ["k"] = "v\r\n" } };

        Assert.Throws<ArgumentException>(() => SendGridPayloadMapper.ToMessage(email, Options));
    }

    [Theory]
    [InlineData("From")]
    [InlineData("to")]
    [InlineData("Subject")]
    [InlineData("Reply-To")]
    [InlineData("BCC")]
    public void Reserved_header_names_are_rejected(string name)
    {
        var email = Basic() with { Headers = new Dictionary<string, string> { [name] = "x@y.test" } };

        Assert.Throws<ArgumentException>(() => SendGridPayloadMapper.ToMessage(email, Options));
    }

    [Fact]
    public void Oversized_custom_args_are_rejected()
    {
        var email = Basic() with { CustomArgs = new Dictionary<string, string> { ["big"] = new string('x', SendGridPayloadMapper.MaxCustomArgsBytes + 1) } };

        Assert.Throws<ArgumentException>(() => SendGridPayloadMapper.ToMessage(email, Options));
    }
}
