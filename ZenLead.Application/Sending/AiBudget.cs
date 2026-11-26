using System.Globalization;

namespace ZenLead.Application.Sending;

public enum BudgetState { Ok, Warning, Exceeded }

/// <summary>Bound from the "AiBudget" section: a per-workspace monthly soft cap on estimated AI spend (UTC calendar month).</summary>
public class AiBudgetOptions
{
    public const string SectionName = "AiBudget";
    public decimal MonthlyCostCapUsd { get; set; } = 20m;
    public decimal WarnAtFraction { get; set; } = 0.8m;
}

public record AiBudgetStatus(decimal SpentUsd, decimal CapUsd, BudgetState State, DateOnly ResetsOn)
{
    /// <summary>Warning from <c>WarnAtFraction</c> of the cap, Exceeded from the cap itself.</summary>
    public static BudgetState StateFor(decimal spentUsd, AiBudgetOptions o)
        => spentUsd >= o.MonthlyCostCapUsd ? BudgetState.Exceeded
         : spentUsd >= o.MonthlyCostCapUsd * o.WarnAtFraction ? BudgetState.Warning
         : BudgetState.Ok;

    public static DateTime MonthStartUtc(DateTime nowUtc) => new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    public static DateOnly ResetDate(DateTime nowUtc) => DateOnly.FromDateTime(MonthStartUtc(nowUtc).AddMonths(1));
}

/// <summary>
/// Per-workspace monthly AI soft cap. The hard ceiling stays the OpenAI dashboard cap; a global cap across workspaces is F29.7.
/// </summary>
public interface IAiBudget
{
    Task<BudgetState> CheckAsync(Guid workspaceId, CancellationToken ct);
    Task<AiBudgetStatus> GetStatusAsync(Guid workspaceId, CancellationToken ct);
}

public class AiBudgetExceededException(DateOnly resetsOn)
    : InvalidOperationException($"This workspace's monthly AI budget has been used. It resets on {resetsOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.")
{
    public DateOnly ResetsOn { get; } = resetsOn;
}
