import { Observable, distinctUntilChanged, fromEvent, map, startWith } from 'rxjs';
import { ChipTone, StatusChip, parseUtc } from '../campaigns/format';
import { ReplyClassification, ThreadListQuery } from './inbox.models';

/** Tone sets the colour; the icon and label carry the same meaning, so it never relies on colour alone. */
export function classificationChip(c: ReplyClassification): StatusChip {
  switch (c) {
    case 'Interested': return { label: 'Interested', tone: 'good', icon: 'thumb_up' };
    case 'NotInterested': return { label: 'Not interested', tone: 'neutral', icon: 'thumb_down' };
    case 'Unsubscribe': return { label: 'Unsubscribe', tone: 'bad', icon: 'unsubscribe' };
    case 'OutOfOffice': return { label: 'Out of office', tone: 'info', icon: 'beach_access' };
    case 'Bounce': return { label: 'Bounce', tone: 'bad', icon: 'report' };
    default: return { label: 'Unclassified', tone: 'neutral' as ChipTone, icon: 'help_outline' };
  }
}

export const NEEDS_REVIEW_CHIP: StatusChip = { label: 'Needs review', tone: 'warn', icon: 'rate_review' };

/** The single-select filter chips; each maps to one API filter. */
export type InboxFilter = 'all' | 'unread' | 'needsReview' | ReplyClassification;

export const FILTERS: { key: InboxFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'unread', label: 'Unread' },
  { key: 'needsReview', label: 'Needs review' },
  { key: 'Interested', label: 'Interested' },
  { key: 'NotInterested', label: 'Not interested' },
  { key: 'Unsubscribe', label: 'Unsubscribe' },
  { key: 'OutOfOffice', label: 'Out of office' },
  { key: 'Unclassified', label: 'Unclassified' }
];

export function isFilter(value: unknown): value is InboxFilter {
  return FILTERS.some(f => f.key === value);
}

/** UI filter state → API query. */
export function toThreadQuery(filter: InboxFilter, page: number, pageSize: number, campaignId?: string, q?: string): ThreadListQuery {
  const query: ThreadListQuery = { page, pageSize };
  if (filter === 'unread') query.unread = true;
  else if (filter === 'needsReview') query.needsReview = true;
  else if (filter !== 'all') query.classification = filter;
  if (campaignId) query.campaignId = campaignId;
  if (q) query.q = q;
  return query;
}

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600], ['month', 30 * 24 * 3600], ['week', 7 * 24 * 3600], ['day', 24 * 3600], ['hour', 3600], ['minute', 60]
];

/** "5 minutes ago", "yesterday", "now". */
export function relativeTime(iso: string, now: Date = new Date()): string {
  const seconds = Math.round((parseUtc(iso).getTime() - now.getTime()) / 1000);
  const fmt = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
  for (const [unit, size] of UNITS)
    if (Math.abs(seconds) >= size) return fmt.format(Math.round(seconds / size), unit);
  return fmt.format(0, 'second');
}

/** Emits whether the tab is visible: now, and on every change. */
export function pageVisible$(doc: Document): Observable<boolean> {
  return fromEvent(doc, 'visibilitychange').pipe(
    startWith(null),
    map(() => doc.visibilityState !== 'hidden'),
    distinctUntilChanged());
}

/** Keeps the previous object for rows that did not change, so a poll re-renders only what moved (tracked by id). */
export function mergeById<T extends { id: string }>(previous: T[], fresh: T[]): T[] {
  const old = new Map(previous.map(p => [p.id, p]));
  return fresh.map(f => {
    const p = old.get(f.id);
    return p && JSON.stringify(p) === JSON.stringify(f) ? p : f;
  });
}
