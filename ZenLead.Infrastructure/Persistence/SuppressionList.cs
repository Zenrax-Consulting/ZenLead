using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// The workspace's suppression list. Every call takes the workspace explicitly (<c>ForWorkspace</c>), so it works from jobs and
/// from the anonymous unsubscribe endpoint as well as from requests.
/// </summary>
public class SuppressionList(ZenLeadDbContext db, TimeProvider clock) : ISuppressionList
{
    private const int InChunk = 500;

    public Task<bool> ContainsAsync(Guid workspaceId, string email, CancellationToken ct)
    {
        var normalized = Normalize(email);
        return db.SuppressedEmails.ForWorkspace(workspaceId).AnyAsync(s => s.Email == normalized, ct);
    }

    public async Task<IReadOnlySet<string>> FilterSuppressedAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in emails.Select(Normalize).Distinct().Chunk(InChunk))
        {
            var found = await db.SuppressedEmails.ForWorkspace(workspaceId).AsNoTracking()
                .Where(s => chunk.Contains(s.Email)).Select(s => s.Email).ToListAsync(ct);
            result.UnionWith(found);
        }
        return result;
    }

    public async Task AddAsync(Guid workspaceId, string email, SuppressionReason reason, Guid? leadId, CancellationToken ct)
    {
        var normalized = Normalize(email);
        if (await ContainsAsync(workspaceId, normalized, ct)) return;      // first reason wins; re-adding is a no-op
        var row = new SuppressedEmail
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Email = normalized, Reason = reason,
            CreatedAt = clock.GetUtcNow().UtcDateTime, SourceLeadId = leadId
        };
        db.SuppressedEmails.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            if (!await ContainsAsync(workspaceId, normalized, ct)) throw;   // a concurrent add won the unique (WorkspaceId, Email) race: fine
        }
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
