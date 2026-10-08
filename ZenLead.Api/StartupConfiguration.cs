using System.Text;

namespace ZenLead.Api;

public static class StartupConfiguration
{
    public const int MinSigningKeyBytes = 32; // HMAC-SHA256 needs a 256-bit key

    private static readonly string[] RequiredKeys =
        ["ConnectionStrings:Default", "Jwt:SigningKey", "Jwt:Issuer", "Jwt:Audience", "OpenAI:ApiKey"];

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

        foreach (var key in RequiredKeys)
            if (string.IsNullOrWhiteSpace(configuration[key]))
                problems.Add($"Missing configuration '{key}'. Set it with: dotnet user-secrets set \"{key}\" \"<value>\" --project ZenLead.Api");

        var signingKey = configuration["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) < MinSigningKeyBytes)
            problems.Add($"'Jwt:SigningKey' must be at least {MinSigningKeyBytes} bytes long.");

        return problems;
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
