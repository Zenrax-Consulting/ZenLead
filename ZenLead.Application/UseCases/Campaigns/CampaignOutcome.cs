using ZenLead.Application.Dtos.Campaigns;

namespace ZenLead.Application.UseCases.Campaigns;

public enum CampaignOutcomeKind { Ok, NotFound, Conflict, Invalid }

/// <summary>Result of a campaign use case. The controller maps NotFound → 404, Conflict → 409 { code, message, problems }, Invalid → 400.</summary>
public record CampaignOutcome<T>(CampaignOutcomeKind Kind, T? Value = default, string? Code = null, string? Message = null,
                                 IReadOnlyList<ActivationProblem>? Problems = null)
{
    public static CampaignOutcome<T> Success(T value) => new(CampaignOutcomeKind.Ok, value);
    public static CampaignOutcome<T> NotFound() => new(CampaignOutcomeKind.NotFound);
    public static CampaignOutcome<T> Conflict(string code, string message, IReadOnlyList<ActivationProblem>? problems = null)
        => new(CampaignOutcomeKind.Conflict, default, code, message, problems);
    public static CampaignOutcome<T> Invalid(string message) => new(CampaignOutcomeKind.Invalid, default, "invalid", message);
}

public static class CampaignCodes
{
    public const string CampaignCompleted = "campaign_completed";
    public const string StepInUse = "step_in_use";
    public const string ActivationBlocked = "activation_blocked";
    public const string InvalidTransition = "invalid_transition";
}
