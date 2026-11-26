using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IApprovalRepository
{
    Task AddAsync(RegistrationApproval approval, CancellationToken ct = default);
    Task<RegistrationApproval?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Atomic single use: marks the row used only if it is unused and unexpired. False if another caller won or it expired.</summary>
    Task<bool> TryConsumeAsync(string tokenHash, DateTime now, ApprovalDecision decision, CancellationToken ct = default);

    /// <summary>Expires every unused approval for the user, so only the newest link works.</summary>
    Task InvalidatePendingForUserAsync(Guid userId, DateTime now, CancellationToken ct = default);

    Task<DateTime?> LastCreatedAtAsync(Guid userId, CancellationToken ct = default);
}
