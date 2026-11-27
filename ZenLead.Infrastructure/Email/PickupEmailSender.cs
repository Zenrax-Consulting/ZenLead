using System.Text.Json;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Email;

/// <summary>
/// Development/E2E stand-in used when Email:Provider is "Pickup": writes each message as one JSON file to a folder, so automated
/// tests can read approval links, reset links and campaign mail. Startup refuses this provider outside Development and E2E.
/// The file name is a timestamp plus a GUID, never anything taken from the message, so a subject can't steer where it is written.
/// </summary>
public class PickupEmailSender(string directory, TimeProvider clock) : IEmailSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var message = new PickupEmail(
            Id: "pickup-" + id,
            WrittenAt: now,
            To: email.To,
            From: email.From,
            ReplyTo: email.ReplyTo,
            Subject: email.Subject,
            Text: email.TextBody,
            Html: email.HtmlBody,
            Headers: email.Headers ?? new Dictionary<string, string>(),
            CustomArgs: email.CustomArgs ?? new Dictionary<string, string>(),
            Categories: email.Categories ?? []);

        var path = Path.Combine(Directory, $"{now:yyyyMMddTHHmmssfffZ}-{id:N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(message, Json), ct);
        return SendResult.Ok(message.Id);
    }

    /// <summary>The JSON shape tests read (camelCase).</summary>
    public sealed record PickupEmail(
        string Id, DateTimeOffset WrittenAt, EmailAddress To, EmailAddress? From, EmailAddress? ReplyTo, string Subject, string Text,
        string? Html, IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, string> CustomArgs, IReadOnlyList<string> Categories);
}
