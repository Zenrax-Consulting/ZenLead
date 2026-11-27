using System.Text;

namespace ZenLead.Api;

public static class StartupConfiguration
{
    public const int MinSigningKeyBytes = 32; // HMAC-SHA256 needs a 256-bit key

    /// <summary>The automated end-to-end test environment (T00): fakes only, test-support endpoints mapped.</summary>
    public const string E2EEnvironment = "E2E";

    private static readonly string[] RequiredKeys =
        ["ConnectionStrings:Default", "Jwt:SigningKey", "Jwt:Issuer", "Jwt:Audience", "Unsubscribe:SigningKey"];

    /// <summary>Returns a human-readable problem for every missing or invalid required setting.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, bool isDevelopment = true)
        => Validate(configuration, isDevelopment ? Environments.Development : Environments.Production);

    /// <summary>Same as <see cref="Validate(IConfiguration, bool)"/>, plus the E2E environment's extra rules.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, string environmentName)
    {
        var isDevelopment = string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);
        var isE2E = string.Equals(environmentName, E2EEnvironment, StringComparison.OrdinalIgnoreCase);
        var problems = new List<string>();

        if (isE2E)
        {
            var provider = configuration["LeadSource:Provider"];
            if (!string.IsNullOrWhiteSpace(provider) && !provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
                problems.Add("'LeadSource:Provider' must be 'Fake' in the E2E environment.");
        }
        else if (!isDevelopment)
        {
            var provider = configuration["LeadSource:Provider"];
            if (string.IsNullOrWhiteSpace(provider) || provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
                problems.Add("'LeadSource:Provider' must be set to a real provider (e.g. Pdl) outside Development; 'Fake' is not allowed.");
            else if (!provider.Equals("Pdl", StringComparison.OrdinalIgnoreCase))
                problems.Add($"Unknown 'LeadSource:Provider' '{provider}'. Known: Pdl.");
            else if (string.IsNullOrWhiteSpace(configuration["LeadSource:Pdl:ApiKey"]))
                problems.Add("Missing configuration 'LeadSource:Pdl:ApiKey'. Set it with: dotnet user-secrets set \"LeadSource:Pdl:ApiKey\" \"<value>\" --project ZenLead.Api");
        }

        problems.AddRange(ValidateEmail(configuration, isDevelopment, isE2E));
        problems.AddRange(ValidateAi(configuration, isDevelopment, isE2E));
        if (isE2E) problems.AddRange(ValidateE2E(configuration));

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
    public static IEnumerable<string> ValidateEmail(IConfiguration configuration, bool isDevelopment, bool isE2E = false)
    {
        var provider = configuration["Email:Provider"];
        var isSendGrid = string.Equals(provider, "SendGrid", StringComparison.OrdinalIgnoreCase);
        var isLog = string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "Log", StringComparison.OrdinalIgnoreCase);
        var isPickup = string.Equals(provider, "Pickup", StringComparison.OrdinalIgnoreCase);

        if (!isSendGrid && !isLog && !isPickup)
            yield return $"Unknown 'Email:Provider' '{provider}'. Known: Log, SendGrid, Pickup.";
        else if (isE2E && !isPickup)
            yield return "'Email:Provider' must be 'Pickup' in the E2E environment (tests read emails from Email:PickupDirectory).";
        else if (isPickup && !isDevelopment && !isE2E)
            yield return "'Email:Provider' 'Pickup' is only allowed in Development and E2E; it writes account emails to disk instead of sending them.";
        else if (isPickup && string.IsNullOrWhiteSpace(configuration["Email:PickupDirectory"]))
            yield return "Missing configuration 'Email:PickupDirectory' (required when 'Email:Provider' is 'Pickup').";
        else if (isLog && !isDevelopment)
            yield return "'Email:Provider' must be 'SendGrid' outside Development; the 'Log' provider would write account emails to the log instead of sending them.";
        else if (isSendGrid && string.IsNullOrWhiteSpace(configuration["SendGrid:ApiKey"]))
            yield return "Missing configuration 'SendGrid:ApiKey' (required when 'Email:Provider' is 'SendGrid'). Set it with: dotnet user-secrets set \"SendGrid:ApiKey\" \"<value>\" --project ZenLead.Api";

        // the inbound webhook (F25) authenticates SendGrid with a shared secret in the Inbound Parse URL
        var inboundSecret = configuration["SendGrid:InboundSecret"];
        if (isSendGrid && string.IsNullOrWhiteSpace(inboundSecret))
            yield return "Missing configuration 'SendGrid:InboundSecret' (required when 'Email:Provider' is 'SendGrid'; at least 32 random characters). Set it with: dotnet user-secrets set \"SendGrid:InboundSecret\" \"<value>\" --project ZenLead.Api";
        else if (!string.IsNullOrWhiteSpace(inboundSecret) && inboundSecret.Length < WebhookOptions.MinInboundSecretLength)
            yield return $"'SendGrid:InboundSecret' must be at least {WebhookOptions.MinInboundSecretLength} characters long.";

        // the signed Event Webhook (F28): the verification key from SendGrid's Event Webhook settings
        var eventKey = configuration["SendGrid:EventWebhook:PublicKey"];
        if (isSendGrid && string.IsNullOrWhiteSpace(eventKey))
            yield return "Missing configuration 'SendGrid:EventWebhook:PublicKey' (required when 'Email:Provider' is 'SendGrid'; the Event Webhook verification key). Set it with: dotnet user-secrets set \"SendGrid:EventWebhook:PublicKey\" \"<value>\" --project ZenLead.Api";
        else if (!string.IsNullOrWhiteSpace(eventKey) && !ZenLead.Infrastructure.Email.SendGridEventSignatureVerifier.IsValidPublicKey(eventKey))
            yield return "'SendGrid:EventWebhook:PublicKey' is not a valid base64 EC public key (copy the Verification Key from SendGrid → Mail Settings → Event Webhook).";
        var maxAge = configuration["SendGrid:EventWebhook:MaxAgeHours"];
        if (!string.IsNullOrWhiteSpace(maxAge) && (!int.TryParse(maxAge, out var hours) || hours < 1 || hours > 72))
            yield return "'SendGrid:EventWebhook:MaxAgeHours' must be a whole number of hours between 1 and 72 (default 26; SendGrid retries for 24 h).";

        var parseMode = configuration["Inbound:ParseMode"];
        if (!string.IsNullOrWhiteSpace(parseMode) && !parseMode.Equals("Fields", StringComparison.OrdinalIgnoreCase) && !parseMode.Equals("RawMime", StringComparison.OrdinalIgnoreCase))
            yield return $"Unknown 'Inbound:ParseMode' '{parseMode}'. Known: Fields (default), RawMime.";
    }

    /// <summary>
    /// <c>Ai:Provider</c> is OpenAI (the default; needs <c>OpenAI:ApiKey</c>) or Fake (deterministic stand-ins, Development and E2E only).
    /// </summary>
    public static IEnumerable<string> ValidateAi(IConfiguration configuration, bool isDevelopment, bool isE2E)
    {
        var provider = configuration["Ai:Provider"];
        var isFake = string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase);
        var isOpenAi = string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase);

        if (!isFake && !isOpenAi)
            yield return $"Unknown 'Ai:Provider' '{provider}'. Known: OpenAI, Fake.";
        else if (isE2E && !isFake)
            yield return "'Ai:Provider' must be 'Fake' in the E2E environment (no real OpenAI calls from tests).";
        else if (isFake && !isDevelopment && !isE2E)
            yield return "'Ai:Provider' 'Fake' is only allowed in Development and E2E.";
        else if (isOpenAi && string.IsNullOrWhiteSpace(configuration["OpenAI:ApiKey"]))
            yield return "Missing configuration 'OpenAI:ApiKey'. Set it with: dotnet user-secrets set \"OpenAI:ApiKey\" \"<value>\" --project ZenLead.Api";
    }

    /// <summary>E2E runs only against fakes and deterministically: no real provider keys, no Hangfire server, never Live sending.</summary>
    public static IEnumerable<string> ValidateE2E(IConfiguration configuration)
    {
        foreach (var key in new[] { "SendGrid:ApiKey", "OpenAI:ApiKey", "LeadSource:Pdl:ApiKey" })
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                yield return $"'{key}' must not be set in the E2E environment; tests never call real providers.";
        if (configuration.GetValue("Hangfire:Enabled", true))
            yield return "'Hangfire:Enabled' must be false in the E2E environment; tests run jobs through /api/v1/test/jobs.";
        var mode = configuration["Sending:Mode"];
        if (!string.IsNullOrWhiteSpace(mode) && !mode.Equals("Restricted", StringComparison.OrdinalIgnoreCase))
            yield return "'Sending:Mode' must be 'Restricted' in the E2E environment.";
    }

    public static void ThrowIfInvalid(IConfiguration configuration, bool isDevelopment = true)
        => ThrowIfInvalid(configuration, isDevelopment ? Environments.Development : Environments.Production);

    public static void ThrowIfInvalid(IConfiguration configuration, string environmentName)
    {
        var problems = Validate(configuration, environmentName);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "ZenLead is not configured correctly (see README.md, 'Local setup'):" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", problems));
    }
}
