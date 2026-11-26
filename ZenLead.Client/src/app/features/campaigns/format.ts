import { CampaignStatus, EnrollmentStatus } from './campaigns.models';

export type ChipTone = 'neutral' | 'good' | 'info' | 'warn' | 'bad';
export interface StatusChip { label: string; tone: ChipTone; icon: string; }

/** The API's DateTimes are UTC but may come without an offset; a bare timestamp is read as UTC. */
export function parseUtc(iso: string): Date {
  return new Date(/[zZ]$|[+-]\d\d:?\d\d$/.test(iso) ? iso : `${iso}Z`);
}

/** "26 Nov 2026, 09:30" in the given IANA zone (falls back to the browser zone if the id is unknown). */
export function formatInZone(iso: string | null | undefined, tz: string): string {
  if (!iso) return '—';
  const date = parseUtc(iso);
  try {
    return new Intl.DateTimeFormat(undefined, { timeZone: tz, dateStyle: 'medium', timeStyle: 'short' }).format(date);
  } catch {
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
  }
}

/** Tone drives colour; the icon and label carry the same meaning, so it never relies on colour alone. */
export function statusChip(status: CampaignStatus | EnrollmentStatus): StatusChip {
  switch (status) {
    case 'Draft': return { label: 'Draft', tone: 'neutral', icon: 'edit_note' };
    case 'Active': return { label: 'Active', tone: 'good', icon: 'play_circle' };
    case 'Paused': return { label: 'Paused', tone: 'warn', icon: 'pause_circle' };
    case 'Completed': return { label: 'Completed', tone: 'info', icon: 'check_circle' };
    case 'Replied': return { label: 'Replied', tone: 'good', icon: 'reply' };
    case 'Unsubscribed': return { label: 'Unsubscribed', tone: 'neutral', icon: 'unsubscribe' };
    case 'Bounced': return { label: 'Bounced', tone: 'bad', icon: 'report' };
    case 'Failed': return { label: 'Failed', tone: 'bad', icon: 'error' };
    default: return { label: String(status), tone: 'neutral', icon: 'help' };
  }
}

export function formatUsd(value: number): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}
