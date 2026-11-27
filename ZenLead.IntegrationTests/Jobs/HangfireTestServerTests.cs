using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Jobs;

/// <summary>Probe jobs for <see cref="HangfireTestServerTests"/>; created by Hangfire through the DI activator.</summary>
public sealed class ProbeLog
{
    public List<string> Calls { get; } = [];
}

public sealed class ProbeJob(ProbeLog log)
{
    public void Record(string value) { lock (log) log.Calls.Add(value); }
    public void Fail(string message) => throw new InvalidOperationException(message);
}

/// <summary>
/// The fixture T05 builds on: a real Hangfire server on a <see cref="SqlServerFixture"/> database, used standalone (no API host),
/// jobs activated from a plain service provider, failures visible at once, and a stop/start that keeps queued work.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class HangfireTestServerTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    private readonly ProbeLog _log = new();
    private JobStorage _storage = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _storage = sql.CreateHangfireStorage();        // creates the HangFire schema before the reset sees it
        await sql.ResetAsync();
        _services = new ServiceCollection().AddSingleton(_log).BuildServiceProvider();
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task AnEnqueuedJob_RunsThroughTheDiActivator_AndSucceeds()
    {
        await using var server = new HangfireTestServer(_storage, _services);

        var id = server.Client.Enqueue<ProbeJob>(j => j.Record("hello"));
        var state = await server.WaitForJobAsync(id);

        Assert.Equal(SucceededState.StateName, state.Name);
        Assert.Equal(["hello"], _log.Calls);
    }

    [Fact]
    public async Task AFailingJob_IsFailedAtOnce_WithItsMessage_WhenRetriesAreOff()
    {
        await using var server = new HangfireTestServer(_storage, _services, retryAttempts: 0);

        var id = server.Client.Enqueue<ProbeJob>(j => j.Fail("boom"));
        var state = await server.WaitForJobAsync(id);

        Assert.Equal(FailedState.StateName, state.Name);
        Assert.Equal("boom", server.FailureOf(id));
    }

    [Fact]
    public async Task WithRetries_AFailingJob_IsRetriedTheGivenNumberOfTimes()
    {
        await using var server = new HangfireTestServer(_storage, _services, retryAttempts: 1);

        var id = server.Client.Enqueue<ProbeJob>(j => j.Fail("again"));
        await server.WaitForJobAsync(id, TimeSpan.FromSeconds(30), FailedState.StateName);

        var history = _storage.GetMonitoringApi().JobDetails(id).History;
        Assert.Equal(2, history.Count(h => h.StateName == ProcessingState.StateName));   // first attempt + one retry
    }

    [Fact]
    public async Task JobsEnqueuedWhileNoServerRuns_ArePickedUpWhenOneStarts()
    {
        var client = new BackgroundJobClient(_storage);
        var ids = Enumerable.Range(0, 3).Select(i => client.Enqueue<ProbeJob>(j => j.Record($"job-{i}"))).ToList();

        await using (var server = new HangfireTestServer(_storage, _services, workerCount: 2))
        {
            foreach (var id in ids) Assert.Equal(SucceededState.StateName, (await server.WaitForJobAsync(id)).Name);
            await server.WaitForIdleAsync();
        }

        Assert.Equal(["job-0", "job-1", "job-2"], _log.Calls.Order());
    }

    [Fact]
    public async Task WaitForJob_TimesOut_WithTheJobsStateAndHistoryInTheMessage()
    {
        await using var server = new HangfireTestServer(_storage, _services);
        var id = server.Client.Enqueue<ProbeJob>(j => j.Record("done"));
        await server.WaitForJobAsync(id);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => server.WaitForJobAsync(id, TimeSpan.FromMilliseconds(300), "NoSuchState"));

        Assert.Contains(id, timeout.Message);
        Assert.Contains("it is Succeeded", timeout.Message);
        Assert.Contains("History:", timeout.Message);
    }
}
