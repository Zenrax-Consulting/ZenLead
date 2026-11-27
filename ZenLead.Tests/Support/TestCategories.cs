namespace ZenLead.Tests.Support;

/// <summary>
/// xUnit trait values for <c>[Trait("Category", …)]</c> (T00). Untagged tests are unit tests and run by default; filter the rest in
/// with <c>dotnet test --filter Category=Integration</c>, or out with <c>Category!=Live</c>.
/// </summary>
public static class TestCategories
{
    /// <summary>Needs real SQL Server (LocalDB or a container), Hangfire on SQL Server, or stubbed HTTP providers (T03).</summary>
    public const string Integration = "Integration";

    /// <summary>Needs a running Azurite blob emulator.</summary>
    public const string Azurite = "Azurite";

    /// <summary>Benchmarks and large-data checks (T06); slow.</summary>
    public const string Performance = "Performance";

    /// <summary>Calls a real external provider (OpenAI, SendGrid, PDL). Never part of a default run.</summary>
    public const string Live = "Live";
}
