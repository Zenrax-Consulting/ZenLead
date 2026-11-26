using ZenLead.Application.Abstractions;
using ZenLead.Application.Sending;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Support;

/// <summary>In-memory suppression list (lowercase addresses per workspace).</summary>
public class FakeSuppressionList : ISuppressionList
{
    public HashSet<(Guid Workspace, string Email)> Entries { get; } = [];

    public void Add(Guid workspaceId, string email) => Entries.Add((workspaceId, email.Trim().ToLowerInvariant()));

    public Task<bool> ContainsAsync(Guid workspaceId, string email, CancellationToken ct)
        => Task.FromResult(Entries.Contains((workspaceId, email.Trim().ToLowerInvariant())));

    public Task<IReadOnlySet<string>> FilterSuppressedAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<string>>(emails.Select(e => e.Trim().ToLowerInvariant())
            .Where(e => Entries.Contains((workspaceId, e))).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public Task AddAsync(Guid workspaceId, string email, SuppressionReason reason, Guid? leadId, CancellationToken ct)
    {
        Add(workspaceId, email);
        return Task.CompletedTask;
    }
}

/// <summary>An AI budget whose state the test sets.</summary>
public class FakeAiBudget : IAiBudget
{
    public BudgetState State { get; set; } = BudgetState.Ok;
    public DateOnly ResetsOn { get; set; } = new(2026, 12, 1);
    public int Checks { get; private set; }

    public Task<BudgetState> CheckAsync(Guid workspaceId, CancellationToken ct)
    {
        Checks++;
        return Task.FromResult(State);
    }

    public Task<AiBudgetStatus> GetStatusAsync(Guid workspaceId, CancellationToken ct)
        => Task.FromResult(new AiBudgetStatus(State == BudgetState.Exceeded ? 20m : 1m, 20m, State, ResetsOn));
}
