import { DAY_NAMES, daysToSet } from './campaigns.models';

export interface TimelineItem { order: number; day: number; label: string; }

/** Cumulative day offsets: step 1 is "Day 0"; each later step adds its delay to the previous step's day. */
export function buildTimeline(steps: { order: number; delayDays: number }[]): TimelineItem[] {
  let day = 0;
  return [...steps].sort((a, b) => a.order - b.order).map((s, i) => {
    day += i === 0 ? 0 : s.delayDays;
    return { order: s.order, day, label: `Day ${day}` };
  });
}

/** "Day 0 → Day 3 → Day 7", or "No steps yet". */
export function timelineSummary(steps: { order: number; delayDays: number }[]): string {
  return buildTimeline(steps).map(t => t.label).join(' → ') || 'No steps yet';
}

/** "Mon–Fri", "Tue, Thu", "Every day", "Mon–Wed, Sat". Runs of three or more consecutive days collapse to a range. */
export function daysSummary(days: number): string {
  const set = daysToSet(days);
  if (set.size === 0) return 'No days';
  if (set.size === 7) return 'Every day';
  const parts: string[] = [];
  let i = 0;
  while (i < 7) {
    if (!set.has(i)) { i++; continue; }
    let j = i;
    while (j + 1 < 7 && set.has(j + 1)) j++;
    if (j - i >= 2) parts.push(`${DAY_NAMES[i]}–${DAY_NAMES[j]}`);
    else for (let k = i; k <= j; k++) parts.push(DAY_NAMES[k]);
    i = j + 1;
  }
  return parts.join(', ');
}

/**
 * Honest copy about the window: delays are calendar days in the campaign's zone; sends happen inside the window on allowed days.
 * "Mon–Fri, 09:00–17:00 (Asia/Dubai)".
 */
export function windowSummary(start: string, end: string, days: number, tz: string): string {
  return `${daysSummary(days)}, ${start}–${end} (${tz})`;
}

/** The per-step line in the builder: "Sends right away, between 09:00–17:00 Mon–Fri" / "Sends on Day 3, between …". */
export function stepSendLabel(day: number, start: string, end: string, days: number): string {
  const when = day === 0 ? 'Sends right away' : `Sends on Day ${day}`;
  return `${when}, between ${start}–${end} ${daysSummary(days)}`;
}
