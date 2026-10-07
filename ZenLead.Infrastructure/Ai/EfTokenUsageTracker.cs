using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Ai;

/// <summary>Persists one row per AI call so spend survives restarts. Lightweight forerunner of the Phase 2 AiGenerationLog.</summary>
public class EfTokenUsageTracker(ZenLeadDbContext db, ILogger<EfTokenUsageTracker> logger) : ITokenUsageTracker
{
    public async Task RecordAsync(AiUsageEntry entry, CancellationToken ct = default)
    {
        db.AiUsageLogs.Add(new AiUsageLog
        {
            Id = Guid.NewGuid(),
            WorkspaceId = entry.WorkspaceId,
            Model = entry.Model,
            PromptTokens = entry.PromptTokens,
            CompletionTokens = entry.CompletionTokens,
            EstimatedCostUsd = entry.EstimatedCostUsd,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("AI usage recorded: {Prompt}+{Completion} tokens, ~${Cost} for workspace {WorkspaceId}",
            entry.PromptTokens, entry.CompletionTokens, entry.EstimatedCostUsd, entry.WorkspaceId);
    }

    public async Task<AiUsageSummary> GetSummaryAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var rows = await db.AiUsageLogs.Where(l => l.WorkspaceId == workspaceId)
            .Select(l => new { l.PromptTokens, l.CompletionTokens, l.EstimatedCostUsd })
            .ToListAsync(ct);

        return new AiUsageSummary(
            rows.Count,
            rows.Sum(r => (long)r.PromptTokens + r.CompletionTokens),
            rows.Sum(r => r.EstimatedCostUsd));
    }
}
