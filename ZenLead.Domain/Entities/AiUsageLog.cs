using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

/// <summary>One row per AI call (the parent plan's "AiGenerationLog" is this table, extended rather than renamed).</summary>
public class AiUsageLog : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Model { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
    public AiPurpose Purpose { get; set; } = AiPurpose.Compose;     // Compose | Personalise | ClassifyReply (F26)
    public Guid? CampaignId { get; set; }
    public Guid? EnrollmentId { get; set; }
    public bool Succeeded { get; set; } = true;                     // false: the provider billed a call whose reply was unusable
}
