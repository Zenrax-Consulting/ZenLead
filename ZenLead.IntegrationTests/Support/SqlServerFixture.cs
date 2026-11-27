using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Respawn;
using Respawn.Graph;
using Testcontainers.MsSql;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Support;

public enum SqlServerMode { LocalDb, Container }

/// <summary>
/// One real SQL Server database for a test class or collection (T03; reused by T05, T06 and T10).
/// <list type="bullet">
/// <item><c>ZENLEAD_TEST_SQL=localdb</c> (default): <c>ZenLeadIT_{guid}</c> on <c>(localdb)\mssqllocaldb</c>.</item>
/// <item><c>ZENLEAD_TEST_SQL=container</c>: the same inside one <see cref="MsSqlContainer"/> shared by the whole test run (needs Docker).</item>
/// </list>
/// The schema comes from the EF migrations (<c>MigrateAsync</c>, never <c>EnsureCreated</c>), so every run also tests the migrations.
/// The database is dropped on dispose. <see cref="ResetAsync"/> empties every table between tests with Respawn, keeping
/// <c>__EFMigrationsHistory</c> and the Hangfire schema version, so a test class pays for one create + migrate only.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ModeVariable = "ZENLEAD_TEST_SQL";
    public const string DatabasePrefix = "ZenLeadIT_";
    public const string HangfireSchema = "HangFire";
    private const string LocalDbServer = @"(localdb)\mssqllocaldb";

    private static readonly SemaphoreSlim ContainerLock = new(1, 1);
    private static MsSqlContainer? _container;
    private static int _staleSweepDone;

    private string? _serverConnectionString;

    /// <summary>True (default): apply every migration on initialise. False leaves the database empty, for tests that migrate it themselves (e.g. an upgrade path).</summary>
    public bool Migrate { get; init; } = true;

    public static SqlServerMode Mode => Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant() switch
    {
        null or "" or "localdb" => SqlServerMode.LocalDb,
        "container" => SqlServerMode.Container,
        var other => throw new InvalidOperationException($"{ModeVariable}='{other}' is not supported. Use 'localdb' (default) or 'container'.")
    };

    public string DatabaseName { get; } = $"{DatabasePrefix}{Guid.NewGuid():N}";

    /// <summary>Connection string of this fixture's database.</summary>
    public string ConnectionString { get; private set; } = "";

    public DbContextOptions<ZenLeadDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _serverConnectionString = Mode == SqlServerMode.Container ? await ContainerConnectionStringAsync() : LocalDbConnectionString("master");
        if (Mode == SqlServerMode.LocalDb && Interlocked.Exchange(ref _staleSweepDone, 1) == 0) await DropStaleDatabasesAsync();

        await ExecuteOnServerAsync($"CREATE DATABASE [{DatabaseName}]");
        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = DatabaseName, MultipleActiveResultSets = true }.ConnectionString;
        Options = new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlServer(ConnectionString).Options;

        if (Migrate)
        {
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
        }
    }

    /// <summary>A context whose "current workspace" is <paramref name="workspaceId"/> (null = no workspace, like a job or the super admin).</summary>
    public ZenLeadDbContext CreateContext(Guid? workspaceId = null, params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlServer(ConnectionString).AddInterceptors(interceptors).Options,
            new FakeCurrentWorkspace(workspaceId));

    /// <summary>
    /// Deletes every row in every table except <c>__EFMigrationsHistory</c> and <c>HangFire.Schema</c>. Hangfire's job tables are
    /// emptied too (when the schema exists), so a job left over from an earlier test can never run in the next one.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        // created per call: the Hangfire schema may appear after the first reset, and the metadata query is cheap
        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = [new Table("__EFMigrationsHistory"), new Table(HangfireSchema, "Schema")]
        });
        await respawner.ResetAsync(connection);
    }

    /// <summary>
    /// Hangfire storage on this database with fast polling (the app's 15 s poll would make every job test slow). Creates the
    /// <c>HangFire</c> schema on first use, exactly as the app does.
    /// </summary>
    public JobStorage CreateHangfireStorage()
    {
        HangfireLogging.Silence();
        return new SqlServerStorage(ConnectionString, new SqlServerStorageOptions
        {
            SchemaName = HangfireSchema,
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromMilliseconds(200),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5)
        });
    }

    public async Task DisposeAsync()
    {
        if (_serverConnectionString is null || ConnectionString.Length == 0) return;
        using (var pooled = new SqlConnection(ConnectionString)) SqlConnection.ClearPool(pooled);   // pooled connections would block the DROP
        await ExecuteOnServerAsync($"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """);
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new SqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static string LocalDbConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = LocalDbServer, InitialCatalog = database, IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 60
    }.ConnectionString;

    /// <summary>One container for the whole run; each fixture creates its own database in it. Testcontainers' reaper removes it afterwards.</summary>
    private static async Task<string> ContainerConnectionStringAsync()
    {
        await ContainerLock.WaitAsync();
        try
        {
            if (_container is null)
            {
                var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await container.StartAsync();
                _container = container;
            }
            return _container.GetConnectionString();
        }
        finally { ContainerLock.Release(); }
    }

    /// <summary>A run that crashed never dropped its databases; drop ours that are more than six hours old (never a parallel run's).</summary>
    private async Task DropStaleDatabasesAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_serverConnectionString);
            await connection.OpenAsync();
            var stale = new List<string>();
            await using (var query = new SqlCommand(
                "SELECT name FROM sys.databases WHERE name LIKE @prefix AND create_date < DATEADD(HOUR, -6, GETDATE())", connection))
            {
                query.Parameters.AddWithValue("@prefix", DatabasePrefix.Replace("_", "[_]") + "%");
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync()) stale.Add(reader.GetString(0));
            }
            foreach (var name in stale)
            {
                await using var drop = new SqlCommand(
                    $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", connection) { CommandTimeout = 60 };
                try { await drop.ExecuteNonQueryAsync(); } catch (SqlException) { /* in use or already gone: leave it */ }
            }
        }
        catch (SqlException) { /* best effort only */ }
    }
}
