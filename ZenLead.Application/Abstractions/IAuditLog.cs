namespace ZenLead.Application.Abstractions;

/// <summary>Platform-level audit trail. <paramref name="detail"/> is serialised to JSON — never pass secrets.</summary>
public interface IAuditLog
{
    Task WriteAsync(string action, Guid? actor, string? targetType, Guid? targetId, object? detail, CancellationToken ct = default);
}
