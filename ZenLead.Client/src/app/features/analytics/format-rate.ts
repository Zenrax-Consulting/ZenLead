/** 0.4286 → "42.9%"; null/undefined/NaN/Infinity → "—" (never "NaN%"). One decimal, clamped to 0–100 %. */
export function formatRate(rate: number | null | undefined): string {
  if (rate === null || rate === undefined || !Number.isFinite(rate)) return '—';
  const pct = Math.min(Math.max(rate, 0), 1) * 100;
  return `${pct.toFixed(1)}%`;
}

/** 12345 → "12,345". */
export function formatCount(value: number): string {
  return new Intl.NumberFormat('en-US').format(value);
}

/** Bounce rates at or above this deserve a written warning (deliverability risk). */
export const BOUNCE_WARN_RATE = 0.02;
