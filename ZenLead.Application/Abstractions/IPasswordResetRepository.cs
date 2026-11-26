using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

/// <summary>Password reset tokens (F21). Not tenant rows: scoped by token hash or user id.</summary>
public interface IPasswordResetRepository
{
    /// <summary>Adds the token; also deletes the user's tokens older than 7 days (housekeeping).</summary>
    Task AddAsync(PasswordResetToken token, CancellationToken ct = default);

    Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct = default);

    /// <summary>Expires every unused, unexpired token of the user, so only the newest link works.</summary>
    Task InvalidateUnusedAsync(Guid userId, DateTime now, CancellationToken ct = default);

    Task<PasswordResetToken?> FindByHashAsync(string hash, CancellationToken ct = default);

    /// <summary>Atomic single use: marks the row used only if it is unused and unexpired. False if another caller won or it expired.</summary>
    Task<bool> TryConsumeAsync(string hash, DateTime now, CancellationToken ct = default);
}
