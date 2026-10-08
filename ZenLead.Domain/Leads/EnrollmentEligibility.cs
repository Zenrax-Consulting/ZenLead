using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Leads;

public enum EligibilityKind { Eligible, EligibleWithWarning, Blocked }
public record Eligibility(EligibilityKind Kind, string? Reason);

public static class EnrollmentEligibility
{
    public static Eligibility Check(LeadStatus status, EmailVerificationStatus verification) =>
        LeadStatusRules.IsSuppressed(status) ? new(EligibilityKind.Blocked, $"Lead is {status}")
        : verification == EmailVerificationStatus.Invalid ? new(EligibilityKind.Blocked, "Email marked invalid")
        : verification is EmailVerificationStatus.Unverified or EmailVerificationStatus.Risky
            ? new(EligibilityKind.EligibleWithWarning, $"Email is {verification}")
        : new(EligibilityKind.Eligible, null);
}
