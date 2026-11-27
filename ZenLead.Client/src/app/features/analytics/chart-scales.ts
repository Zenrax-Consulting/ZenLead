/**
 * Scale helpers for the hand-rolled SVG charts (F28 has no chart library). Counts only: the y-axis starts at 0 and ends at a "nice"
 * maximum (1, 2, 2.5 or 5 × 10ⁿ steps) so gridlines land on round numbers.
 */
export interface NiceScale { max: number; step: number; ticks: number[]; }

export function niceScale(dataMax: number, targetTicks = 4): NiceScale {
  if (!Number.isFinite(dataMax) || dataMax <= 0) return { max: 1, step: 1, ticks: [0, 1] };
  const rough = dataMax / targetTicks;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rough)));
  const step = [1, 2, 2.5, 5, 10].map(m => m * magnitude).find(s => s >= rough)!;
  const intStep = Math.max(1, step < 1 ? 1 : step);                     // counts: never fractional ticks
  const max = Math.ceil(dataMax / intStep) * intStep;
  const ticks: number[] = [];
  for (let t = 0; t <= max + 1e-9; t += intStep) ticks.push(Math.round(t * 100) / 100);
  return { max, step: intStep, ticks };
}

/** x positions of N evenly spaced points across [left, right]; a single point sits in the middle. */
export function xPositions(count: number, left: number, right: number): number[] {
  if (count <= 0) return [];
  if (count === 1) return [(left + right) / 2];
  const step = (right - left) / (count - 1);
  return Array.from({ length: count }, (_, i) => left + i * step);
}

/** Value → y pixel for a 0-based scale (top = max, bottom = 0). */
export function yPosition(value: number, max: number, top: number, bottom: number): number {
  const ratio = max <= 0 ? 0 : Math.min(Math.max(value / max, 0), 1);
  return bottom - ratio * (bottom - top);
}

/** Indices of x labels to print so they don't collide: at most `maxLabels`, always including the first and last. */
export function labelIndices(count: number, maxLabels: number): number[] {
  if (count <= 0) return [];
  if (count <= maxLabels) return Array.from({ length: count }, (_, i) => i);
  const every = Math.ceil((count - 1) / (maxLabels - 1));
  const out: number[] = [];
  for (let i = 0; i < count - 1; i += every) out.push(i);
  if (count - 1 - out[out.length - 1] < every / 2 && out.length > 1) out.pop();   // keep the last label clear of its neighbour
  out.push(count - 1);
  return out;
}

/** Nudges end labels apart vertically so they never overlap (sorted by y, min gap in px), clamped to [top, bottom]. */
export function separateLabels(ys: number[], minGap: number, top: number, bottom: number): number[] {
  const order = ys.map((y, i) => ({ y, i })).sort((a, b) => a.y - b.y);
  for (let k = 1; k < order.length; k++)
    if (order[k].y - order[k - 1].y < minGap) order[k].y = order[k - 1].y + minGap;
  const overflow = order.length ? order[order.length - 1].y - bottom : 0;
  if (overflow > 0) for (const o of order) o.y -= overflow;
  for (const o of order) o.y = Math.max(o.y, top);
  const out = new Array<number>(ys.length);
  for (const o of order) out[o.i] = o.y;
  return out;
}
