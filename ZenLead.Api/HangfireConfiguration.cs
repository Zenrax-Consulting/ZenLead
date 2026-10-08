using Hangfire;
using Hangfire.SqlServer;

namespace ZenLead.Api;

public static class HangfireConfiguration
{
    public static IServiceCollection AddZenLeadHangfire(this IServiceCollection services, IConfiguration config)
    {
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(config.GetConnectionString("Default"), new SqlServerStorageOptions
            {
                SchemaName = "HangFire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(15),     // polite to the (free-tier) DB; F30 re-checks auto-pause behaviour
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5)
            }));
        services.AddHangfireServer(o => o.WorkerCount = 2);

        // 3 attempts, exponential-ish; after the last one the job stays visible in the dashboard's Failed list (our dead-letter view)
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute
        {
            Attempts = 3, DelaysInSeconds = [30, 120, 480], OnAttemptsExceeded = AttemptsExceededAction.Fail
        });
        return services;
    }
}
