using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>The EF migrations on real SQL Server (F11 and later): empty → latest, model in sync, and the Phase 1 → F11 upgrade path.</summary>
[Trait("Category", TestCategories.Integration)]
public class MigrationTests : IClassFixture<SqlServerFixture>
{
    private const string Phase1LastMigration = "AddAiUsageLog";
    private const string F11Migration = "AddCompanyAndLeadProvenance";
    private readonly SqlServerFixture _sql;

    public MigrationTests(SqlServerFixture sql) => _sql = sql;

    [Fact]
    public async Task FromEmpty_EveryMigrationApplies_AndNoneIsPending()
    {
        await using var db = _sql.CreateContext();

        var all = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.NotEmpty(all);
        Assert.Equal(all, applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task TheModel_HasNoChangesMissingFromTheMigrations()
    {
        await using var db = _sql.CreateContext();

        // the equivalent of `dotnet ef migrations has-pending-model-changes`
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changed since the last migration. Run `dotnet ef migrations add`.");
    }

    [Fact]
    public async Task Phase1Database_WithAMixedCaseDuplicateEmail_FailsTheF11MigrationLoudly_ThenSucceedsAfterTheDocumentedFix()
    {
        await using var phase1 = new SqlServerFixture { Migrate = false };
        await phase1.InitializeAsync();
        try
        {
            await using (var db = phase1.CreateContext())
                await db.GetService<IMigrator>().MigrateAsync(Phase1LastMigration);
            await ExecuteScriptAsync(phase1.ConnectionString, Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sql", "phase1-baseline.sql"));

            // the duplicate pair (Ann@… / ann@…) collides once F11 lower-cases emails: the migration must stop, not merge or drop rows
            await using (var db = phase1.CreateContext())
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync());
                Assert.Contains("Duplicate (WorkspaceId, Email) leads exist", ex.Message);
                var applied = await db.Database.GetAppliedMigrationsAsync();
                Assert.DoesNotContain(applied, m => m.EndsWith(F11Migration, StringComparison.Ordinal));
            }
            Assert.Equal(4, await ScalarAsync(phase1.ConnectionString, "SELECT COUNT(*) FROM Leads"));   // nothing was lost

            // the documented fix: merge or delete one of the pair, then re-run
            // (by id: the column is case-insensitive, so WHERE Email = 'ANN@…' would match both rows)
            await ExecuteAsync(phase1.ConnectionString, "DELETE FROM Leads WHERE Id = 'aaaaaaaa-0000-0000-0000-000000000002'");
            await using (var db = phase1.CreateContext())
            {
                await db.Database.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            Assert.Equal(3, await ScalarAsync(phase1.ConnectionString, "SELECT COUNT(*) FROM Leads"));
            Assert.Equal(0, await ScalarAsync(phase1.ConnectionString, "SELECT COUNT(*) FROM Leads WHERE Email <> LOWER(LTRIM(RTRIM(Email)))"));
        }
        finally { await phase1.DisposeAsync(); }
    }

    private static async Task ExecuteScriptAsync(string connectionString, string path)
    {
        var batches = (await File.ReadAllTextAsync(path)).Split(["\nGO", "\r\nGO"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b))) await ExecuteAsync(connectionString, batch);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
