using System.Text.Json;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Infrastructure.Persistence;

public class EfAuditLog(ZenLeadDbContext db, TimeProvider clock) : IAuditLog
{
    public async Task WriteAsync(string action, Guid? actor, string? targetType, Guid? targetId, object? detail, CancellationToken ct = default)
    {
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = clock.GetUtcNow().UtcDateTime,
            ActorUserId = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail)
        });
        await db.SaveChangesAsync(ct);
    }
}
