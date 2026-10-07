using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

public class RefreshTokenService(ZenLeadDbContext db) : IRefreshTokenService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // housekeeping: drop this user's tokens that are expired or were revoked a while ago
        await db.RefreshTokens
            .Where(t => t.UserId == userId && (t.ExpiresAt < now || (t.RevokedAt != null && t.RevokedAt < now.AddDays(-1))))
            .ExecuteDeleteAsync(ct);

        var (raw, hash) = GenerateTokenPair();
        db.RefreshTokens.Add(NewToken(userId, hash, now));
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null) return new RefreshResult(false, Guid.Empty, null, "Token not found.");
        if (existing.RevokedAt is not null) return new RefreshResult(false, Guid.Empty, null, "Token revoked.");
        if (existing.ExpiresAt < DateTime.UtcNow) return new RefreshResult(false, Guid.Empty, null, "Token expired.");

        // Atomic claim: only one concurrent caller can flip RevokedAt from NULL, so a token is never rotated twice.
        var now = DateTime.UtcNow;
        var claimed = await db.RefreshTokens
            .Where(t => t.Id == existing.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (claimed == 0) return new RefreshResult(false, Guid.Empty, null, "Token revoked.");

        var (newRaw, newHash) = GenerateTokenPair();
        db.RefreshTokens.Add(NewToken(existing.UserId, newHash, now));
        await db.SaveChangesAsync(ct);

        return new RefreshResult(true, existing.UserId, newRaw, null);
    }

    private static RefreshToken NewToken(Guid userId, string hash, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = hash,
        CreatedAt = now,
        ExpiresAt = now.Add(Lifetime)
    };

    private static (string Raw, string Hash) GenerateTokenPair()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes);
        return (raw, Hash(raw));
    }

    private static string Hash(string raw)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
