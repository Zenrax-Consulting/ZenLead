using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support;

/// <summary>Real ASP.NET Identity (UserManager + EF stores) over in-memory SQLite, with captured log output.</summary>
public sealed class IdentityTestHost : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _root;

    public CapturingLoggerProvider Logs { get; } = new();

    public IdentityTestHost(Action<SuperAdminOptions>? superAdmin = null)
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddScoped<ICurrentWorkspace>(_ => new FakeCurrentWorkspace());
        services.AddDbContext<ZenLeadDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<AppUser>(o =>
            {
                // mirror Program.cs
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<ZenLeadDbContext>();
        services.Configure<SuperAdminOptions>(o => superAdmin?.Invoke(o));
        services.AddScoped<SuperAdminSeeder>();
        services.AddScoped<IIdentityService, IdentityService>();
        _root = services.BuildServiceProvider();

        using var scope = _root.CreateScope();
        scope.ServiceProvider.GetRequiredService<ZenLeadDbContext>().Database.EnsureCreated();
    }

    /// <summary>A fresh scope per call, like a separate request or app start.</summary>
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _root.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task RunAsync(Func<IServiceProvider, Task> action) => RunAsync(async sp => { await action(sp); return true; });

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public string All => string.Join(Environment.NewLine, Messages);

    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
