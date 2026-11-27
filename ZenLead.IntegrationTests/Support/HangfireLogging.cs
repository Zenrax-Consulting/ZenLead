using Hangfire.Logging;

namespace ZenLead.IntegrationTests.Support;

/// <summary>
/// Hangfire keeps one process-wide log provider. Hangfire.AspNetCore points it at the host's <c>ILoggerFactory</c> when the app
/// configures Hangfire, so after that test host is disposed, any later <c>new SqlServerStorage(...)</c> in the same test run throws
/// <see cref="ObjectDisposedException"/>. Test code switches it to a no-op provider instead (Hangfire logs aren't asserted on).
/// </summary>
public static class HangfireLogging
{
    public static void Silence() => LogProvider.SetCurrentLogProvider(NoOpProvider.Instance);

    private sealed class NoOpProvider : ILogProvider, ILog
    {
        public static readonly NoOpProvider Instance = new();
        public ILog GetLogger(string name) => this;
        public bool Log(LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null) => false;
    }
}
