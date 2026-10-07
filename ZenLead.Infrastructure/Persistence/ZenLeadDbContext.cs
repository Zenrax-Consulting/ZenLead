using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class ZenLeadDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public ZenLeadDbContext(DbContextOptions<ZenLeadDbContext> options) : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // required — maps Identity's own tables
        builder.ApplyConfigurationsFromAssembly(typeof(ZenLeadDbContext).Assembly);
    }
}
