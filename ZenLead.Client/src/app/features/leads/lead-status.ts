import { LEAD_STATUSES, LeadStatus } from './leads.models';

const isSuppressed = (s: LeadStatus) => s === 'Unsubscribed' || s === 'Bounced';

/** Mirrors ZenLead.Domain LeadStatusRules.CanTransition. */
export function canTransition(from: LeadStatus, to: LeadStatus): boolean {
  if (from === to) return true;
  if (isSuppressed(from)) return false;
  switch (to) {
    case 'New': return false;
    case 'Contacted': return from === 'New';
    case 'Replied':
    case 'Unsubscribed':
    case 'Bounced': return true;
    default: return false;
  }
}

/** Statuses a lead may be moved to from `current` (including itself). Suppressed statuses allow only themselves. */
export function allowedStatuses(current: LeadStatus): LeadStatus[] {
  return LEAD_STATUSES.filter(to => canTransition(current, to));
}
