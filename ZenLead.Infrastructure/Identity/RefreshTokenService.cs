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
        var (raw, hash) = GenerateTokenPair();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public async Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
    {
        var hash = Hash(rawToken);
        var existing = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null) return new RefreshResult(false, Guid.Empty, null, "Token not found.");
        if (existing.RevokedAt is not null) return new RefreshResult(false, Guid.Empty, null, "Token revoked.");
        if (existing.ExpiresAt < DateTime.UtcNow) return new RefreshResult(false, Guid.Empty, null, "Token expired.");

        existing.RevokedAt = DateTime.UtcNow;
        var (newRaw, newHash) = GenerateTokenPair();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = existing.UserId,
            TokenHash = newHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        });
        await db.SaveChangesAsync(ct);

        return new RefreshResult(true, existing.UserId, newRaw, null);
    }

    private static (string Raw, string Hash) GenerateTokenPair()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes);
        return (raw, Hash(raw));
    }

    private static string Hash(string raw)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
