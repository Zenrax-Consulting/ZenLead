namespace ZenLead.Domain.Entities;

public class AiUsageLog
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Model { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTime CreatedAt { get; set; }
}
