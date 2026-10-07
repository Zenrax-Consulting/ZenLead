using System.Text;

namespace ZenLead.Api;

public static class StartupConfiguration
{
    public const int MinSigningKeyBytes = 32; // HMAC-SHA256 needs a 256-bit key

    private static readonly string[] RequiredKeys =
        ["ConnectionStrings:Default", "Jwt:SigningKey", "Jwt:Issuer", "Jwt:Audience", "OpenAI:ApiKey"];

    /// <summary>Returns a human-readable problem for every missing or invalid required setting.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        var problems = new List<string>();

        foreach (var key in RequiredKeys)
            if (string.IsNullOrWhiteSpace(configuration[key]))
                problems.Add($"Missing configuration '{key}'. Set it with: dotnet user-secrets set \"{key}\" \"<value>\" --project ZenLead.Api");

        var signingKey = configuration["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) < MinSigningKeyBytes)
            problems.Add($"'Jwt:SigningKey' must be at least {MinSigningKeyBytes} bytes long.");

        return problems;
    }

    public static void ThrowIfInvalid(IConfiguration configuration)
    {
        var problems = Validate(configuration);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "ZenLead is not configured correctly (see README.md, 'Local setup'):" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", problems));
    }
}
