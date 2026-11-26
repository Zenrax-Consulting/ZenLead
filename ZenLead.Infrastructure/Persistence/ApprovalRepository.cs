using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>Registration approvals are not tenant rows: every query is scoped by token hash or user id (F20).</summary>
public class ApprovalRepository(ZenLeadDbContext db) : IApprovalRepository
{
    public async Task AddAsync(RegistrationApproval approval, CancellationToken ct = default)
    {
        db.RegistrationApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
    }

    public Task<RegistrationApproval?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
        => db.RegistrationApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.TokenHash == tokenHash, ct);

    public async Task<bool> TryConsumeAsync(string tokenHash, DateTime now, ApprovalDecision decision, CancellationToken ct = default)
        => await db.RegistrationApprovals
            .Where(a => a.TokenHash == tokenHash && a.UsedAt == null && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedAt, now).SetProperty(a => a.Decision, decision), ct) == 1;

    public Task InvalidatePendingForUserAsync(Guid userId, DateTime now, CancellationToken ct = default)
        => db.RegistrationApprovals
            .Where(a => a.UserId == userId && a.UsedAt == null && a.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, now), ct);

    public Task<DateTime?> LastCreatedAtAsync(Guid userId, CancellationToken ct = default)
        => db.RegistrationApprovals.Where(a => a.UserId == userId).MaxAsync(a => (DateTime?)a.CreatedAt, ct);
}
