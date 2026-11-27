using Microsoft.Extensions.Configuration;
using ZenLead.Api;

namespace ZenLead.Tests.Api;

/// <summary>Environment rules added by T00: Pickup email and fake AI only in Development/E2E, and E2E only with fakes.</summary>
public class StartupConfigurationEnvironmentTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> Common() => new()
    {
        ["ConnectionStrings:Default"] = "Server=(localdb)\\mssqllocaldb;Database=x;",
        ["Jwt:SigningKey"] = new string('k', 32),
        ["Jwt:Issuer"] = "zenlead",
        ["Jwt:Audience"] = "zenlead-client",
        ["Unsubscribe:SigningKey"] = new string('u', 32),
        ["App:PublicBaseUrl"] = "https://localhost:52618"
    };

    /// <summary>What the E2E runner provides.</summary>
    private static Dictionary<string, string?> E2E()
    {
        var values = Common();
        values["Email:Provider"] = "Pickup";
        values["Email:PickupDirectory"] = "../.e2e/mail";
        values["Ai:Provider"] = "Fake";
        values["LeadSource:Provider"] = "Fake";
        values["Hangfire:Enabled"] = "false";
        values["Sending:Mode"] = "Restricted";
        return values;
    }

    private static Dictionary<string, string?> ProductionLike()
    {
        var values = Common();
        values["OpenAI:ApiKey"] = "sk-test";
        values["Email:Provider"] = "SendGrid";
        values["SendGrid:ApiKey"] = "SG.test";
        values["SendGrid:InboundSecret"] = new string('s', 32);
        values["SendGrid:EventWebhook:PublicKey"] = ZenLead.Tests.Support.EventWebhookSigner.Shared.PublicKey;
        values["LeadSource:Provider"] = "Pdl";
        values["LeadSource:Pdl:ApiKey"] = "pdl";
        return values;
    }

    [Fact]
    public void E2E_RunnerConfiguration_HasNoProblems()
        => Assert.Empty(StartupConfiguration.Validate(Config(E2E()), "E2E"));

    [Fact]
    public void Production_Baseline_HasNoProblems()
        => Assert.Empty(StartupConfiguration.Validate(Config(ProductionLike()), "Production"));

    [Theory]
    [InlineData("SendGrid:ApiKey", "SG.real")]
    [InlineData("OpenAI:ApiKey", "sk-real")]
    [InlineData("LeadSource:Pdl:ApiKey", "pdl-real")]
    public void E2E_RealProviderKeyPresent_FailsStartup(string key, string value)
    {
        var values = E2E();
        values[key] = value;
        Assert.Contains(StartupConfiguration.Validate(Config(values), "E2E"), p => p.Contains($"'{key}' must not be set in the E2E environment"));
    }

    [Theory]
    [InlineData("Email:Provider", "Log", "'Email:Provider' must be 'Pickup'")]
    [InlineData("Email:Provider", "SendGrid", "'Email:Provider' must be 'Pickup'")]
    [InlineData("Ai:Provider", "OpenAI", "'Ai:Provider' must be 'Fake'")]
    [InlineData("Ai:Provider", null, "'Ai:Provider' must be 'Fake'")]
    [InlineData("LeadSource:Provider", "Pdl", "'LeadSource:Provider' must be 'Fake'")]
    [InlineData("Hangfire:Enabled", "true", "'Hangfire:Enabled' must be false")]
    [InlineData("Hangfire:Enabled", null, "'Hangfire:Enabled' must be false")]
    [InlineData("Sending:Mode", "Live", "'Sending:Mode' must be 'Restricted'")]
    public void E2E_WithoutFakes_FailsStartup(string key, string? value, string expected)
    {
        var values = E2E();
        values[key] = value;
        Assert.Contains(StartupConfiguration.Validate(Config(values), "E2E"), p => p.Contains(expected));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Pickup_OutsideDevelopmentAndE2E_FailsStartup(string environment)
    {
        var values = ProductionLike();
        values["Email:Provider"] = "Pickup";
        values["Email:PickupDirectory"] = "mail";
        Assert.Contains(StartupConfiguration.Validate(Config(values), environment), p => p.Contains("'Pickup' is only allowed in Development and E2E"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void FakeAi_OutsideDevelopmentAndE2E_FailsStartup(string environment)
    {
        var values = ProductionLike();
        values["Ai:Provider"] = "Fake";
        Assert.Contains(StartupConfiguration.Validate(Config(values), environment), p => p.Contains("'Ai:Provider' 'Fake' is only allowed"));
    }

    [Fact]
    public void Development_AllowsPickupAndFakeAi_WithoutAnOpenAiKey()
    {
        var values = Common();
        values["Email:Provider"] = "Pickup";
        values["Email:PickupDirectory"] = "mail";
        values["Ai:Provider"] = "Fake";
        Assert.Empty(StartupConfiguration.Validate(Config(values), "Development"));
    }

    [Fact]
    public void Pickup_WithoutDirectory_FailsStartup()
    {
        var values = E2E();
        values["Email:PickupDirectory"] = null;
        Assert.Contains(StartupConfiguration.Validate(Config(values), "E2E"), p => p.Contains("Email:PickupDirectory"));
    }

    [Fact]
    public void OpenAiProvider_StillRequiresItsKey()
    {
        var values = Common();
        Assert.Contains(StartupConfiguration.Validate(Config(values), "Development"), p => p.Contains("Missing configuration 'OpenAI:ApiKey'"));
    }

    [Fact]
    public void UnknownAiProvider_FailsStartup()
    {
        var values = Common();
        values["Ai:Provider"] = "Gemini";
        Assert.Contains(StartupConfiguration.Validate(Config(values), "Development"), p => p.Contains("Unknown 'Ai:Provider'"));
    }
}
