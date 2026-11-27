using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// A real Hangfire <see cref="BackgroundJobServer"/> on a test database (T03; used heavily by T05). Jobs are activated from the
/// given service provider, exactly as in the app, so a test can enqueue through the app's own <c>IJobScheduler</c> and wait for
/// the job to finish. Stop it (dispose) to simulate a crash or restart, and start a new one to resume.
/// </summary>
public sealed class HangfireTestServer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly BackgroundJobServer _server;

    /// <param name="storage">Usually <see cref="SqlServerFixture.CreateHangfireStorage"/>, or the app's registered <see cref="JobStorage"/>.</param>
    /// <param name="services">Where job classes are resolved from (one scope per job, like the app).</param>
    /// <param name="retryAttempts">
    /// Null keeps the app's global retry filter (3 attempts, 30 s and more apart). A number replaces it for this server, with one
    /// second between attempts; 0 makes the first exception final, so a test sees <see cref="FailedState"/> at once.
    /// </param>
    public HangfireTestServer(JobStorage storage, IServiceProvider services, int workerCount = 1, int? retryAttempts = 0)
    {
        Storage = storage;
        _server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            ServerName = $"it-{Guid.NewGuid():N}"[..12],
            WorkerCount = workerCount,
            SchedulePollingInterval = TimeSpan.FromSeconds(1),         // retries and scheduled jobs become visible quickly
            Activator = new AspNetCoreJobActivator(services.GetRequiredService<IServiceScopeFactory>()),
            FilterProvider = retryAttempts is { } attempts ? FiltersWithRetry(attempts) : null
        }, storage);
    }

    /// <summary>A server over the app's own registered <see cref="JobStorage"/> and services (e.g. <c>IntegrationApiFactory.Services</c>).</summary>
    public static HangfireTestServer For(IServiceProvider services, int workerCount = 1, int? retryAttempts = 0)
        => new(services.GetRequiredService<JobStorage>(), services, workerCount, retryAttempts);

    public JobStorage Storage { get; }

    public IBackgroundJobClient Client => new BackgroundJobClient(Storage);

    /// <summary>
    /// Polls until the job reaches one of <paramref name="states"/> (default: Succeeded, Failed or Deleted) and returns that state.
    /// Throws <see cref="TimeoutException"/> with the job's current state and history if it doesn't get there in time.
    /// </summary>
    public async Task<StateData> WaitForJobAsync(string jobId, TimeSpan? timeout = null, params string[] states)
    {
        if (states.Length == 0) states = [SucceededState.StateName, FailedState.StateName, DeletedState.StateName];
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            using (var connection = Storage.GetConnection())
            {
                var state = connection.GetStateData(jobId);
                if (state is not null && states.Contains(state.Name, StringComparer.OrdinalIgnoreCase)) return state;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Job {jobId} did not reach {string.Join("/", states)} in time; it is {state?.Name ?? "unknown"}. {History(jobId)}");
            }
            await Task.Delay(100);
        }
    }

    /// <summary>Waits until no job is enqueued, processing or (optionally) scheduled.</summary>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null, bool includeScheduled = false)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var monitoring = Storage.GetMonitoringApi();
        while (true)
        {
            var stats = monitoring.GetStatistics();
            if (stats.Enqueued == 0 && stats.Processing == 0 && (!includeScheduled || stats.Scheduled == 0)) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Hangfire still busy: {stats.Enqueued} enqueued, {stats.Processing} processing, {stats.Scheduled} scheduled.");
            await Task.Delay(100);
        }
    }

    /// <summary>The exception message of a failed job, for assertions and readable test output.</summary>
    public string? FailureOf(string jobId)
    {
        var details = Storage.GetMonitoringApi().JobDetails(jobId);
        return details?.History.FirstOrDefault(h => h.StateName == FailedState.StateName)?.Data is { } data && data.TryGetValue("ExceptionMessage", out var message) ? message : null;
    }

    private string History(string jobId)
    {
        var details = Storage.GetMonitoringApi().JobDetails(jobId);
        if (details is null) return "No such job.";
        return "History: " + string.Join(" <- ", details.History.Select(h =>
            h.StateName + (h.Data.TryGetValue("ExceptionMessage", out var m) ? $" ({m})" : h.Reason is null ? "" : $" ({h.Reason})")));
    }

    private static IJobFilterProvider FiltersWithRetry(int attempts)
    {
        var filters = new JobFilterCollection();
        // everything the app registered globally except its retry policy, which this server replaces
        foreach (var filter in GlobalJobFilters.Filters.Where(f => f.Instance is not AutomaticRetryAttribute)) filters.Add(filter.Instance, filter.Order);
        filters.Add(new AutomaticRetryAttribute
        {
            Attempts = attempts, DelaysInSeconds = Enumerable.Repeat(1, Math.Max(attempts, 1)).ToArray(), OnAttemptsExceeded = AttemptsExceededAction.Fail
        });
        return new JobFilterProviderCollection(filters, new JobFilterAttributeFilterProvider());
    }

    public async ValueTask DisposeAsync()
    {
        _server.SendStop();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await _server.WaitForShutdownAsync(cts.Token); }
        catch (OperationCanceledException) { /* a stuck job: dispose anyway */ }
        _server.Dispose();
    }
}
