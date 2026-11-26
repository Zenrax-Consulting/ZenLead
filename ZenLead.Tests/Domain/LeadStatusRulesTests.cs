using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Tests.Domain;

public class LeadStatusRulesTests
{
    [Theory]
    [InlineData(LeadStatus.New, false)]
    [InlineData(LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.Replied, false)]
    [InlineData(LeadStatus.Unsubscribed, true)]
    [InlineData(LeadStatus.Bounced, true)]
    public void IsSuppressed(LeadStatus status, bool expected)
        => Assert.Equal(expected, LeadStatusRules.IsSuppressed(status));

    [Theory]
    [InlineData(LeadStatus.New, LeadStatus.New, true)]
    [InlineData(LeadStatus.New, LeadStatus.Contacted, true)]
    [InlineData(LeadStatus.New, LeadStatus.Replied, true)]
    [InlineData(LeadStatus.New, LeadStatus.Unsubscribed, true)]
    [InlineData(LeadStatus.New, LeadStatus.Bounced, true)]
    [InlineData(LeadStatus.Contacted, LeadStatus.New, false)]
    [InlineData(LeadStatus.Contacted, LeadStatus.Replied, true)]
    [InlineData(LeadStatus.Contacted, LeadStatus.Unsubscribed, true)]
    [InlineData(LeadStatus.Replied, LeadStatus.New, false)]
    [InlineData(LeadStatus.Replied, LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.Replied, LeadStatus.Bounced, true)]
    [InlineData(LeadStatus.Unsubscribed, LeadStatus.Unsubscribed, true)]
    [InlineData(LeadStatus.Unsubscribed, LeadStatus.New, false)]
    [InlineData(LeadStatus.Unsubscribed, LeadStatus.Replied, false)]
    [InlineData(LeadStatus.Bounced, LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.Bounced, LeadStatus.Unsubscribed, false)]
    public void CanTransition(LeadStatus from, LeadStatus to, bool expected)
        => Assert.Equal(expected, LeadStatusRules.CanTransition(from, to));

    [Theory]
    [InlineData(LeadStatus.Replied, LeadStatus.Contacted, true)]
    [InlineData(LeadStatus.Replied, LeadStatus.New, false)]
    [InlineData(LeadStatus.Contacted, LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.Unsubscribed, LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.Bounced, LeadStatus.Contacted, false)]
    [InlineData(LeadStatus.New, LeadStatus.Contacted, false)]
    public void CanRevertReply_only_allows_Replied_back_to_Contacted(LeadStatus current, LeadStatus previous, bool expected)
        => Assert.Equal(expected, LeadStatusRules.CanRevertReply(current, previous));

    [Fact]
    public void CanTransition_still_refuses_Replied_to_Contacted()
        => Assert.False(LeadStatusRules.CanTransition(LeadStatus.Replied, LeadStatus.Contacted));
}
