import { ChangeDetectionStrategy, ChangeDetectorRef, Component, Input, OnChanges } from '@angular/core';
import { TrendPoint } from '../analytics.models';
import { labelIndices, niceScale, separateLabels, xPositions, yPosition } from '../chart-scales';
import { formatCount } from '../format-rate';

export type SeriesKey = 'sent' | 'opened' | 'replies';
export type MarkerShape = 'circle' | 'square' | 'triangle';

/** Fixed order = fixed colour: Sent is always --chart-1, Opened --chart-2, Replies --chart-3 (validated as a set, light and dark). */
export const SERIES: { key: SeriesKey; label: string; color: string; shape: MarkerShape }[] = [
  { key: 'sent', label: 'Sent', color: 'var(--chart-1)', shape: 'circle' },
  { key: 'opened', label: 'Opened ≈', color: 'var(--chart-2)', shape: 'square' },
  { key: 'replies', label: 'Replies', color: 'var(--chart-3)', shape: 'triangle' }
];

/** Above this many days per-point markers are dropped (lines only) and only the end markers remain. */
export const MAX_MARKED_POINTS = 31;

interface RenderedSeries {
  key: SeriesKey; label: string; color: string; shape: MarkerShape;
  path: string; points: { x: number; y: number; value: number }[];
  end: { x: number; y: number; labelY: number; value: number };
}

/** "2026-03-05" → "Mar 5" (calendar day, no zone shift). */
export function shortDay(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(y, m - 1, d)));
}

export function longDay(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  return new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(y, m - 1, d)));
}

/** The text alternative for the chart: totals and the busiest day. */
export function describeTrend(points: TrendPoint[]): string {
  if (points.length === 0) return 'No data.';
  const sum = (k: SeriesKey) => points.reduce((a, p) => a + p[k], 0);
  const busiest = points.reduce((best, p) => (p.sent > best.sent ? p : best), points[0]);
  const range = `${longDay(points[0].day)} to ${longDay(points[points.length - 1].day)}`;
  const peak = busiest.sent > 0 ? ` Busiest day: ${longDay(busiest.day)} with ${formatCount(busiest.sent)} sent.` : '';
  return `Daily emails from ${range}: ${formatCount(sum('sent'))} sent, ${formatCount(sum('opened'))} opened (approximate), ${formatCount(sum('replies'))} replies.${peak}`;
}

/**
 * Daily Sent / Opened ≈ / Replies as a small SVG line chart (no chart library). Identity never relies on colour: each line has a direct
 * end label, a distinct marker shape and a legend; hovering or focusing a day shows exact numbers; "View as table" swaps in an
 * accessible table of the same data.
 */
@Component({
  selector: 'app-trend-chart',
  standalone: false,
  templateUrl: './trend-chart.html',
  styleUrl: './trend-chart.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TrendChart implements OnChanges {
  @Input({ required: true }) points: TrendPoint[] = [];
  @Input() heading = 'Daily activity';
  @Input() chartId = 'trend';

  readonly width = 640;
  readonly height = 240;
  readonly margin = { top: 16, right: 104, bottom: 28, left: 44 };
  readonly series = SERIES;
  readonly fmt = formatCount;
  readonly shortDay = shortDay;
  readonly longDay = longDay;

  showTable = false;
  hoverIndex: number | null = null;

  rendered: RenderedSeries[] = [];
  ticks: { value: number; y: number }[] = [];
  xs: number[] = [];
  xLabels: { x: number; text: string }[] = [];
  description = '';
  empty = true;
  marked = true;

  constructor(private cdr: ChangeDetectorRef) {}

  get plotLeft() { return this.margin.left; }
  get plotRight() { return this.width - this.margin.right; }
  get plotTop() { return this.margin.top; }
  get plotBottom() { return this.height - this.margin.bottom; }
  get columnWidth() { return this.xs.length > 1 ? this.xs[1] - this.xs[0] : this.plotRight - this.plotLeft; }

  ngOnChanges(): void {
    const pts = this.points ?? [];
    this.empty = pts.length === 0 || pts.every(p => p.sent === 0 && p.opened === 0 && p.replies === 0);
    this.description = describeTrend(pts);
    this.marked = pts.length <= MAX_MARKED_POINTS;
    this.hoverIndex = null;

    const scale = niceScale(Math.max(0, ...pts.flatMap(p => [p.sent, p.opened, p.replies])));
    this.ticks = scale.ticks.map(value => ({ value, y: yPosition(value, scale.max, this.plotTop, this.plotBottom) }));
    this.xs = xPositions(pts.length, this.plotLeft, this.plotRight);
    this.xLabels = labelIndices(pts.length, 6).map(i => ({ x: this.xs[i], text: shortDay(pts[i].day) }));

    const base = SERIES.map(s => {
      const points = pts.map((p, i) => ({ x: this.xs[i], y: yPosition(p[s.key], scale.max, this.plotTop, this.plotBottom), value: p[s.key] }));
      const path = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ');
      const last = points[points.length - 1] ?? { x: this.plotRight, y: this.plotBottom, value: 0 };
      return { ...s, path, points, end: { ...last, labelY: last.y } };
    });
    const labelYs = separateLabels(base.map(s => s.end.y), 16, this.plotTop + 4, this.plotBottom);
    this.rendered = base.map((s, i) => ({ ...s, end: { ...s.end, labelY: labelYs[i] } }));
    this.cdr.markForCheck();
  }

  toggleTable(): void {
    this.showTable = !this.showTable;
    this.cdr.markForCheck();
  }

  hover(index: number | null): void {
    this.hoverIndex = index;
    this.cdr.markForCheck();
  }

  /** Arrow keys walk the days when the plot has focus (keyboard equivalent of hovering). */
  onKey(event: KeyboardEvent): void {
    if (this.points.length === 0) return;
    const last = this.points.length - 1;
    const current = this.hoverIndex ?? -1;
    let next: number | null = null;
    if (event.key === 'ArrowRight') next = Math.min(last, current + 1);
    else if (event.key === 'ArrowLeft') next = current < 0 ? last : Math.max(0, current - 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = last;
    else if (event.key === 'Escape') next = null;
    else return;
    event.preventDefault();
    this.hover(next);
  }

  /** Tooltip box position as a percentage of the chart width (flips to the left of the crosshair past the middle). */
  tooltipStyle(index: number): Record<string, string> {
    const pct = (this.xs[index] / this.width) * 100;
    return pct > 55 ? { right: `${100 - pct + 2}%` } : { left: `${pct + 2}%` };
  }

  markerPath(shape: MarkerShape, x: number, y: number, r = 4.5): string {
    switch (shape) {
      case 'square': return `M${x - r},${y - r} h${2 * r} v${2 * r} h${-2 * r} Z`;
      case 'triangle': return `M${x},${y - r - 1} L${x + r + 1},${y + r} L${x - r - 1},${y + r} Z`;
      default: return `M${x - r},${y} a${r},${r} 0 1,0 ${2 * r},0 a${r},${r} 0 1,0 ${-2 * r},0`;
    }
  }
}
