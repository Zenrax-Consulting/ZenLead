using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>Password reset tokens are not tenant rows: every query is scoped by token hash or user id (F21).</summary>
public class PasswordResetRepository(ZenLeadDbContext db) : IPasswordResetRepository
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public async Task AddAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        var cutoff = token.CreatedAt - KeepFor;
        await db.PasswordResetTokens.Where(t => t.UserId == token.UserId && t.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync(ct);
    }

    public Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct = default)
        => db.PasswordResetTokens.CountAsync(t => t.UserId == userId && t.CreatedAt >= since, ct);

    public Task InvalidateUnusedAsync(Guid userId, DateTime now, CancellationToken ct = default)
        => db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now), ct);

    public Task<PasswordResetToken?> FindByHashAsync(string hash, CancellationToken ct = default)
        => db.PasswordResetTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

    public async Task<bool> TryConsumeAsync(string hash, DateTime now, CancellationToken ct = default)
        => await db.PasswordResetTokens
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct) == 1;
}
