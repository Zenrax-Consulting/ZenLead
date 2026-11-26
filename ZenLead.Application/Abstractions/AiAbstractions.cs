using ZenLead.Domain.Enums;

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

    /// <summary>The reply classifier's model (F26), "OpenAI:ClassifierModel".</summary>
    public string ClassifierModel { get; set; } = "gpt-4o-mini";

    /// <summary>Per-model prices ("OpenAI:Models:{name}"); a model not listed here is priced at <see cref="Model"/>'s prices.</summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o-mini"] = new ModelPrice { PricePer1KInputUsd = 0.00015m, PricePer1KOutputUsd = 0.0006m }
    };

    public decimal EstimateCostUsd(string model, int promptTokens, int completionTokens)
    {
        if (string.Equals(model, Model, StringComparison.OrdinalIgnoreCase) || !Models.TryGetValue(model, out var price))
            return EstimateCostUsd(promptTokens, completionTokens);
        return promptTokens / 1000m * price.PricePer1KInputUsd + completionTokens / 1000m * price.PricePer1KOutputUsd;
    }
}

public class ModelPrice
{
    public decimal PricePer1KInputUsd { get; set; }
    public decimal PricePer1KOutputUsd { get; set; }
}

/// <param name="Succeeded">False when the provider billed a call whose reply was unusable (the spend still counts).</param>
public record AiUsageEntry(Guid WorkspaceId, string Model, int PromptTokens, int CompletionTokens, decimal EstimatedCostUsd,
    AiPurpose Purpose = AiPurpose.Compose, Guid? CampaignId = null, Guid? EnrollmentId = null, bool Succeeded = true);
public record AiUsageSummary(int Calls, long TotalTokens, decimal EstimatedCostUsd);

public interface ITokenUsageTracker
{
    Task RecordAsync(AiUsageEntry entry, CancellationToken ct = default);
    Task<AiUsageSummary> GetSummaryAsync(Guid workspaceId, CancellationToken ct = default);
}
