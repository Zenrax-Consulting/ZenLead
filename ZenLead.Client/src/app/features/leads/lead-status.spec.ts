import { allowedStatuses, canTransition } from './lead-status';
import { LEAD_STATUSES, LeadStatus } from './leads.models';

// Same cases as the backend LeadStatusRulesTests / LeadStatusRules.CanTransition table.
describe('lead status transitions', () => {
  it.each<[LeadStatus, LeadStatus[]]>([
    ['New', ['New', 'Contacted', 'Replied', 'Unsubscribed', 'Bounced']],
    ['Contacted', ['Contacted', 'Replied', 'Unsubscribed', 'Bounced']],
    ['Replied', ['Replied', 'Unsubscribed', 'Bounced']],
    ['Unsubscribed', ['Unsubscribed']],
    ['Bounced', ['Bounced']]
  ])('from %s allows %j', (from, expected) => {
    expect(allowedStatuses(from)).toEqual(expected);
  });

  it('never goes back to New', () => {
    LEAD_STATUSES.filter(s => s !== 'New').forEach(s => expect(canTransition(s, 'New')).toBe(false));
  });
});
