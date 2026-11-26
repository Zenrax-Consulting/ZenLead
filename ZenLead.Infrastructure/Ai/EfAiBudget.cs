using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenLead.Application.Sending;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Ai;

/// <summary>Sums the workspace's estimated AI spend since the first of the UTC month and compares it to the soft cap.</summary>
public class EfAiBudget(ZenLeadDbContext db, IOptions<AiBudgetOptions> options, TimeProvider clock) : IAiBudget
{
    public async Task<BudgetState> CheckAsync(Guid workspaceId, CancellationToken ct)
        => (await GetStatusAsync(workspaceId, ct)).State;

    public async Task<AiBudgetStatus> GetStatusAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = AiBudgetStatus.MonthStartUtc(now);
        // summed client-side: SQLite (tests) can't aggregate decimals; a month of one workspace's rows is small
        var costs = await db.AiUsageLogs.ForWorkspace(workspaceId).AsNoTracking()
            .Where(l => l.CreatedAt >= since).Select(l => l.EstimatedCostUsd).ToListAsync(ct);
        var spent = costs.Sum();
        var o = options.Value;
        return new AiBudgetStatus(spent, o.MonthlyCostCapUsd, AiBudgetStatus.StateFor(spent, o), AiBudgetStatus.ResetDate(now));
    }
}
