using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Tests.Domain;

public class EnrollmentEligibilityTests
{
    [Theory]
    [InlineData(LeadStatus.New, EmailVerificationStatus.Verified, EligibilityKind.Eligible)]
    [InlineData(LeadStatus.Contacted, EmailVerificationStatus.Verified, EligibilityKind.Eligible)]
    [InlineData(LeadStatus.Replied, EmailVerificationStatus.Verified, EligibilityKind.Eligible)]
    [InlineData(LeadStatus.New, EmailVerificationStatus.Unverified, EligibilityKind.EligibleWithWarning)]
    [InlineData(LeadStatus.New, EmailVerificationStatus.Risky, EligibilityKind.EligibleWithWarning)]
    [InlineData(LeadStatus.New, EmailVerificationStatus.Invalid, EligibilityKind.Blocked)]
    [InlineData(LeadStatus.Unsubscribed, EmailVerificationStatus.Verified, EligibilityKind.Blocked)]
    [InlineData(LeadStatus.Bounced, EmailVerificationStatus.Verified, EligibilityKind.Blocked)]
    [InlineData(LeadStatus.Bounced, EmailVerificationStatus.Invalid, EligibilityKind.Blocked)]
    public void Check(LeadStatus status, EmailVerificationStatus verification, EligibilityKind expected)
    {
        var result = EnrollmentEligibility.Check(status, verification);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(expected == EligibilityKind.Eligible, result.Reason is null);
    }
}
