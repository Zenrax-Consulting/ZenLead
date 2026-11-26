using System.Text.Json;

namespace ZenLead.Tests.Support;

/// <summary>Loads <c>Fixtures/Inbound/*.json</c> (SendGrid Inbound Parse field dictionaries) and fills in the placeholders.</summary>
public static class InboundFixtures
{
    public const string InboundDomain = "reply.zenlead.test";

    public static readonly string[] FieldModeFixtures =
        ["gmail-reply", "outlook-reply", "apple-mail-reply", "ooo-autoreply", "dsn-bounce", "non-utf8"];

    public static Dictionary<string, string> Load(string name, Guid? enrollmentId = null, string leadEmail = "jane@example.com")
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Inbound", name + ".json");
        var json = File.ReadAllText(path)
            .Replace("__ENROLLMENT__", (enrollmentId ?? Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")).ToString("N"))
            .Replace("__LEAD__", leadEmail);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
    }
}
