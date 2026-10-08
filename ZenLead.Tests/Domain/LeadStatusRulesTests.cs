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
}
