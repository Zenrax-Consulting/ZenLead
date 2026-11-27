using Azure;
using Azure.Storage.Blobs;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// Azurite blob storage on the default local endpoint (<c>UseDevelopmentStorage=true</c>, port 10000), or the connection string in
/// <c>ZENLEAD_TEST_AZURITE</c> (e.g. Azurite in a CI service container). Tests call
/// <see cref="SkipIfUnavailable"/> first: when Azurite isn't running they are reported as skipped with a clear message, never failed.
/// Each fixture instance (one per test class) gets its own container, deleted afterwards.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "ZENLEAD_TEST_AZURITE";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } custom ? custom : "UseDevelopmentStorage=true";

    public BlobServiceClient Service { get; } = new(ConnectionString, new BlobClientOptions { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(5) } });
    public string ContainerName { get; } = $"it-{Guid.NewGuid():N}";
    public BlobContainerClient Container => Service.GetBlobContainerClient(ContainerName);

    /// <summary>Null when Azurite answered; otherwise why the tests are skipped.</summary>
    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Container.CreateIfNotExistsAsync(cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is RequestFailedException or OperationCanceledException or AggregateException or HttpRequestException)
        {
            UnavailableReason = $"Azurite is not reachable at {Service.Uri} ({ex.GetType().Name}). Start it with `azurite --silent --location %TEMP%\\azurite` " +
                                $"(or set {ConnectionStringVariable}) to run this test.";
        }
    }

    public void SkipIfUnavailable() => Skip.If(UnavailableReason is not null, UnavailableReason);

    public async Task DisposeAsync()
    {
        if (UnavailableReason is not null) return;
        try { await Container.DeleteIfExistsAsync(); } catch (RequestFailedException) { /* best effort */ }
    }
}
