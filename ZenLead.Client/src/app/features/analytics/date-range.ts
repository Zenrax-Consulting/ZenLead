import { DateRangeParams } from './analytics.models';

export type RangePreset = '7d' | '30d' | '90d';
export const PRESETS: { value: RangePreset; label: string; days: number }[] = [
  { value: '7d', label: 'Last 7 days', days: 7 },
  { value: '30d', label: 'Last 30 days', days: 30 },
  { value: '90d', label: 'Last 90 days', days: 90 }
];
export const DEFAULT_PRESET: RangePreset = '30d';
export const MAX_RANGE_DAYS = 366;

/** What the range bar shows: a preset, or a custom from/to. Kept in the URL as `?range=30d` or `?from=&to=`. */
export type RangeSelection = { kind: 'preset'; preset: RangePreset } | { kind: 'custom'; from: string; to: string };

const ISO = /^\d{4}-\d{2}-\d{2}$/;

/** Local calendar date → `yyyy-MM-dd` (no UTC shift). */
export function toIsoDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** `yyyy-MM-dd` → a local Date at midnight, or null when malformed / not a real date. */
export function parseIsoDate(s: string | null | undefined): Date | null {
  if (!s || !ISO.test(s)) return null;
  const [y, m, d] = s.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  return date.getFullYear() === y && date.getMonth() === m - 1 && date.getDate() === d ? date : null;
}

/** Inclusive day count between two ISO dates (DST-safe: compares calendar days, not milliseconds of local time). */
export function daysInclusive(from: string, to: string): number {
  const a = parseIsoDate(from)!, b = parseIsoDate(to)!;
  return Math.round((Date.UTC(b.getFullYear(), b.getMonth(), b.getDate()) - Date.UTC(a.getFullYear(), a.getMonth(), a.getDate())) / 86_400_000) + 1;
}

/** Null when valid, otherwise a message for the user. */
export function validateRange(from: string | null | undefined, to: string | null | undefined): string | null {
  if (!parseIsoDate(from) || !parseIsoDate(to)) return 'Choose a start and end date.';
  if (from! > to!) return 'The start date must be on or before the end date.';
  if (daysInclusive(from!, to!) > MAX_RANGE_DAYS) return `Choose at most ${MAX_RANGE_DAYS} days.`;
  return null;
}

/**
 * The from/to the API gets. Presets end "today" in the browser's calendar; the API interprets the days in the workspace zone,
 * which can differ by a day near midnight — acceptable for a rolling window.
 */
export function toParams(selection: RangeSelection, today: Date = new Date()): DateRangeParams {
  if (selection.kind === 'custom') return { from: selection.from, to: selection.to };
  const days = PRESETS.find(p => p.value === selection.preset)!.days;
  const from = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (days - 1));
  return { from: toIsoDate(from), to: toIsoDate(today) };
}

/** URL query params → selection. Anything invalid falls back to the default preset. */
export function parseSelection(params: { get(name: string): string | null }): RangeSelection {
  const from = params.get('from'), to = params.get('to');
  if (from && to && validateRange(from, to) === null) return { kind: 'custom', from, to };
  const range = params.get('range');
  const preset = PRESETS.find(p => p.value === range)?.value ?? DEFAULT_PRESET;
  return { kind: 'preset', preset };
}

/** Selection → query params (the other form's keys cleared, so the URL never carries both). */
export function selectionToQuery(selection: RangeSelection): Record<string, string | null> {
  return selection.kind === 'preset'
    ? { range: selection.preset, from: null, to: null }
    : { range: null, from: selection.from, to: selection.to };
}
