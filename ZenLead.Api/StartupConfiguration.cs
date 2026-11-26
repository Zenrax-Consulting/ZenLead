using System.Text;

namespace ZenLead.Api;

public static class StartupConfiguration
{
    public const int MinSigningKeyBytes = 32; // HMAC-SHA256 needs a 256-bit key

    private static readonly string[] RequiredKeys =
        ["ConnectionStrings:Default", "Jwt:SigningKey", "Jwt:Issuer", "Jwt:Audience", "OpenAI:ApiKey", "Unsubscribe:SigningKey"];

    /// <summary>Returns a human-readable problem for every missing or invalid required setting.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, bool isDevelopment = true)
    {
        var problems = new List<string>();

        if (!isDevelopment)
        {
            var provider = configuration["LeadSource:Provider"];
            if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
                problems.Add("'LeadSource:Provider' must be set to a real provider (e.g. Pdl) outside Development; 'Fake' is not allowed.");
            else if (!provider.Equals("Pdl", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Unknown 'LeadSource:Provider' '{provider}'. Known: Pdl.");
            else if (string.IsNullOrWhiteSpace(configuration["LeadSource:Pdl:ApiKey"]))
                problems.Add("Missing configuration 'LeadSource:Pdl:ApiKey'. Set it with: dotnet user-secrets set \"LeadSource:Pdl:ApiKey\" \"<value>\" --project ZenLead.Api");
        }

        problems.AddRange(ValidateEmail(configuration, isDevelopment));

        // fail closed: a typo must never silently become Live (or anything else)
        var mode = configuration["Sending:Mode"];
        if (!string.IsNullOrWhiteSpace(mode) && !mode.Equals("Restricted", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Live", StringComparison.OrdinalIgnoreCase))
            problems.Add($"Unknown 'Sending:Mode' '{mode}'. Known: Restricted (default; only Sending:RecipientAllowList receives campaign email), Live.");
        if (!isDevelopment && !Uri.TryCreate(configuration["App:PublicBaseUrl"], UriKind.Absolute, out _))
            problems.Add("'App:PublicBaseUrl' must be set to the absolute public URL of the app outside Development (used for links in emails).");

        foreach (var key in RequiredKeys)
            if (string.IsNullOrWhiteSpace(configuration[key]))
                problems.Add($"Missing configuration '{key}'. Set it with: dotnet user-secrets set \"{key}\" \"<value>\" --project ZenLead.Api");

        var signingKey = configuration["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) < MinSigningKeyBytes)
            problems.Add($"'Jwt:SigningKey' must be at least {MinSigningKeyBytes} bytes long.");

        // signs unsubscribe links (F22); they never expire, so rotating this key breaks every link already sent
        var unsubscribeKey = configuration["Unsubscribe:SigningKey"];
        if (!string.IsNullOrWhiteSpace(unsubscribeKey) && Encoding.UTF8.GetByteCount(unsubscribeKey) < MinSigningKeyBytes)
            problems.Add($"'Unsubscribe:SigningKey' must be at least {MinSigningKeyBytes} bytes long.");

        return problems;
    }

    /// <summary>
    /// SendGrid needs its API key; outside Development the provider must be SendGrid, because the "Log" fallback
    /// would write password-reset and approval links to the server log instead of sending them.
    /// </summary>
    public static IEnumerable<string> ValidateEmail(IConfiguration configuration, bool isDevelopment)
    {
        var provider = configuration["Email:Provider"];
        var isSendGrid = string.Equals(provider, "SendGrid", StringComparison.OrdinalIgnoreCase);
        var isLog = string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "Log", StringComparison.OrdinalIgnoreCase);

        if (!isSendGrid && !isLog)
            yield return $"Unknown 'Email:Provider' '{provider}'. Known: Log, SendGrid.";
        else if (isLog && !isDevelopment)
            yield return "'Email:Provider' must be 'SendGrid' outside Development; the 'Log' provider would write account emails to the log instead of sending them.";
        else if (isSendGrid && string.IsNullOrWhiteSpace(configuration["SendGrid:ApiKey"]))
            yield return "Missing configuration 'SendGrid:ApiKey' (required when 'Email:Provider' is 'SendGrid'). Set it with: dotnet user-secrets set \"SendGrid:ApiKey\" \"<value>\" --project ZenLead.Api";
    }

    public static void ThrowIfInvalid(IConfiguration configuration, bool isDevelopment = true)
    {
        var problems = Validate(configuration, isDevelopment);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "ZenLead is not configured correctly (see README.md, 'Local setup'):" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", problems));
    }
}
