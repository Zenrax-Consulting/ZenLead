using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Email;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Email;

public sealed class PickupEmailSenderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenlead-pickup-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Send_WritesOneJsonFileWithEveryField()
    {
        var dir = Path.Combine(_root, "mail");
        var sender = new PickupEmailSender(dir, _clock);
        var email = new OutboundEmail(
            new EmailAddress("jane@example.test", "Jane"), "Approve Jane", "Text body", "<p>Html</p>",
            From: new EmailAddress("business@zenraxconsulting.com", "Zenrax"), ReplyTo: new EmailAddress("r-1@reply.example.test"),
            Headers: new Dictionary<string, string> { ["List-Unsubscribe"] = "<https://x.test/u>" },
            CustomArgs: new Dictionary<string, string> { ["emailMessageId"] = "m-1", ["workspaceId"] = "w-1" },
            Categories: ["campaign"]);

        var result = await sender.SendAsync(email);

        Assert.True(result.Success);
        Assert.StartsWith("pickup-", result.ProviderMessageId);
        var file = Assert.Single(Directory.GetFiles(dir, "*.json"));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var json = doc.RootElement;
        Assert.Equal(result.ProviderMessageId, json.GetProperty("id").GetString());
        Assert.Equal("jane@example.test", json.GetProperty("to").GetProperty("address").GetString());
        Assert.Equal("business@zenraxconsulting.com", json.GetProperty("from").GetProperty("address").GetString());
        Assert.Equal("r-1@reply.example.test", json.GetProperty("replyTo").GetProperty("address").GetString());
        Assert.Equal("Approve Jane", json.GetProperty("subject").GetString());
        Assert.Equal("Text body", json.GetProperty("text").GetString());
        Assert.Equal("<p>Html</p>", json.GetProperty("html").GetString());
        Assert.Equal("<https://x.test/u>", json.GetProperty("headers").GetProperty("List-Unsubscribe").GetString());
        Assert.Equal("m-1", json.GetProperty("customArgs").GetProperty("emailMessageId").GetString());
        Assert.Equal("campaign", json.GetProperty("categories")[0].GetString());
        Assert.Equal(_clock.GetUtcNow(), json.GetProperty("writtenAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Send_CreatesTheFolderIfMissing()
    {
        var dir = Path.Combine(_root, "a", "b", "c");
        await new PickupEmailSender(dir, _clock).SendAsync(new OutboundEmail(new EmailAddress("x@example.test"), "s", "t"));
        Assert.Single(Directory.GetFiles(dir));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("..\\..\\escape")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\evil")]
    [InlineData("con")]
    public async Task Send_NeverDerivesThePathFromTheMessage(string subject)
    {
        var dir = Path.Combine(_root, "mail");
        await new PickupEmailSender(dir, _clock).SendAsync(new OutboundEmail(new EmailAddress("x@example.test"), subject, "t"));

        var file = Assert.Single(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(file));
        Assert.Matches(@"^\d{8}T\d{9}Z-[0-9a-f]{32}\.json$", Path.GetFileName(file));
    }

    [Fact]
    public async Task Send_ManyMessages_EachGetsItsOwnFile()
    {
        var sender = new PickupEmailSender(_root, _clock);              // same timestamp for all of them
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => sender.SendAsync(new OutboundEmail(new EmailAddress($"x{i}@example.test"), "s", "t"))));
        Assert.Equal(20, Directory.GetFiles(_root, "*.json").Length);
    }
}
