using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class ZenLeadDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public const string TenantFilter = "Tenant";
    public const string SoftDeleteFilter = "SoftDelete";

    private readonly ICurrentWorkspace _currentWorkspace;

    public ZenLeadDbContext(DbContextOptions<ZenLeadDbContext> options, ICurrentWorkspace currentWorkspace) : base(options)
        => _currentWorkspace = currentWorkspace;

    // Read through a property of the context so EF treats it as a per-context parameter, not a constant baked into the model.
    private Guid? CurrentWorkspaceId => _currentWorkspace.WorkspaceId;

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<TargetProfile> TargetProfiles => Set<TargetProfile>();
    public DbSet<LeadDiscoveryRun> DiscoveryRuns => Set<LeadDiscoveryRun>();
    public DbSet<CsvImportBatch> CsvImportBatches => Set<CsvImportBatch>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // required — maps Identity's own tables
        builder.ApplyConfigurationsFromAssembly(typeof(ZenLeadDbContext).Assembly);

        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
            SetTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);

        builder.Entity<Lead>().HasQueryFilter(SoftDeleteFilter, l => l.DeletedAt == null);
    }

    private static readonly MethodInfo SetTenantFilterMethod =
        typeof(ZenLeadDbContext).GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void SetTenantFilter<T>(ModelBuilder builder) where T : class, ITenantEntity
        => builder.Entity<T>().HasQueryFilter(TenantFilter, e => e.WorkspaceId == CurrentWorkspaceId);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        StampTenant();
        return base.SaveChangesAsync(ct);
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    /// <summary>New tenant rows with no workspace get the caller's. Rows that already carry one (jobs, ingestion) are left alone.</summary>
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>().Where(e => e.State == EntityState.Added && e.Entity.WorkspaceId == Guid.Empty))
            entry.Entity.WorkspaceId = CurrentWorkspaceId
                ?? throw new InvalidOperationException($"{entry.Entity.GetType().Name} has no WorkspaceId and there is no current workspace.");
    }
}
