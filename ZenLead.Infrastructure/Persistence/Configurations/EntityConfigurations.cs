using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence.Configurations;

public class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.Property(w => w.Name).HasMaxLength(200);
    }
}

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Domain).HasMaxLength(253);
        builder.Property(c => c.Industry).HasMaxLength(200);
        builder.Property(c => c.Country).HasMaxLength(100);
        builder.Property(c => c.Size).HasMaxLength(50);
        builder.HasIndex(c => new { c.WorkspaceId, c.Domain }).IsUnique().HasFilter("[Domain] IS NOT NULL");
        builder.HasIndex(c => new { c.WorkspaceId, c.Name });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.Property(l => l.Name).HasMaxLength(200);
        builder.Property(l => l.Email).HasMaxLength(256);
        builder.Property(l => l.Title).HasMaxLength(200);
        builder.HasIndex(l => l.WorkspaceId);
        builder.HasIndex(l => new { l.WorkspaceId, l.Email }).IsUnique();     // includes soft-deleted rows on purpose
        builder.HasIndex(l => new { l.WorkspaceId, l.SourceRunId });
        builder.HasIndex(l => new { l.WorkspaceId, l.CreatedAt });
        builder.HasIndex(l => new { l.WorkspaceId, l.Status });
        builder.HasIndex(l => new { l.WorkspaceId, l.CompanyId });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(l => l.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Company).WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.Property(t => t.TokenHash).HasMaxLength(64); // Base64 SHA-256 = 44 chars
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.HasIndex(u => u.WorkspaceId);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(u => u.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AiUsageLogConfiguration : IEntityTypeConfiguration<AiUsageLog>
{
    public void Configure(EntityTypeBuilder<AiUsageLog> builder)
    {
        builder.Property(l => l.Model).HasMaxLength(100);
        builder.Property(l => l.EstimatedCostUsd).HasPrecision(18, 6);
        builder.HasIndex(l => l.WorkspaceId);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(l => l.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
