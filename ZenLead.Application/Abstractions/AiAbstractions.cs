namespace ZenLead.Application.Abstractions;

public enum AiProviderFailureKind
{
    RateLimited,
    Unavailable,
    InvalidResponse
}

/// <summary>Provider-neutral AI failure, so Api/Application never see Semantic Kernel / OpenAI exception types.</summary>
public class AiProviderException(
    AiProviderFailureKind kind, string message, Exception? inner = null, int promptTokens = 0, int completionTokens = 0)
    : Exception(message, inner)
{
    public AiProviderFailureKind Kind { get; } = kind;

    /// <summary>Tokens the provider billed before the failure (e.g. a reply that could not be parsed). Zero if none were consumed.</summary>
    public int PromptTokens { get; } = promptTokens;
    public int CompletionTokens { get; } = completionTokens;
}

/// <summary>Model name and per-1K-token USD prices used to estimate cost. Bound from the "OpenAI" config section.</summary>
public class AiPricing
{
    public string Model { get; set; } = "gpt-4o";
    public decimal PricePer1KInputUsd { get; set; } = 0.0025m;
    public decimal PricePer1KOutputUsd { get; set; } = 0.01m;

    public decimal EstimateCostUsd(int promptTokens, int completionTokens)
        => promptTokens / 1000m * PricePer1KInputUsd + completionTokens / 1000m * PricePer1KOutputUsd;
}

public record AiUsageEntry(Guid WorkspaceId, string Model, int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd);
public record AiUsageSummary(int Calls, long TotalTokens, decimal EstimatedCostUsd);

public interface ITokenUsageTracker
{
    Task RecordAsync(AiUsageEntry entry, CancellationToken ct = default);
    Task<AiUsageSummary> GetSummaryAsync(Guid workspaceId, CancellationToken ct = default);
}
