import { ChangeDetectionStrategy, ChangeDetectorRef, Component, Input, OnChanges } from '@angular/core';
import { StepBreakdown } from '../analytics.models';
import { formatCount } from '../format-rate';

export const BAR_MEASURES: { key: 'sent' | 'delivered' | 'opened' | 'replies'; label: string }[] = [
  { key: 'sent', label: 'Sent' },
  { key: 'delivered', label: 'Delivered' },
  { key: 'opened', label: 'Opened ≈' },
  { key: 'replies', label: 'Replies' }
];

export interface BarRow { step: StepBreakdown; title: string; bars: { label: string; value: number; pct: number }[]; }

/** Rows ordered by step order (removed steps, order 0, last); bars share one scale (the largest Sent), so steps compare honestly. */
export function buildBarRows(steps: StepBreakdown[]): BarRow[] {
  const max = Math.max(1, ...steps.map(s => Math.max(s.sent, s.delivered, s.opened, s.replies)));
  return [...steps]
    .sort((a, b) => (a.order || Number.MAX_SAFE_INTEGER) - (b.order || Number.MAX_SAFE_INTEGER))
    .map(step => ({
      step,
      title: step.order > 0 ? `Step ${step.order} · ${truncate(step.subjectTemplate, 60)}` : truncate(step.subjectTemplate, 60),
      bars: BAR_MEASURES.map(m => ({ label: m.label, value: step[m.key], pct: (step[m.key] / max) * 100 }))
    }));
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : `${s.slice(0, max - 1)}…`;
}

/**
 * The per-step breakdown as a horizontal bar list: one group per step, one labelled bar per measure with the number printed at the
 * bar end. One hue throughout: each bar is named by its text label, so nothing depends on colour. "View as table" shows the same data.
 */
@Component({
  selector: 'app-bar-list',
  standalone: false,
  templateUrl: './bar-list.html',
  styleUrl: './bar-list.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class BarList implements OnChanges {
  @Input({ required: true }) steps: StepBreakdown[] = [];
  @Input() heading = 'By step';

  rows: BarRow[] = [];
  showTable = false;
  readonly fmt = formatCount;

  constructor(private cdr: ChangeDetectorRef) {}

  ngOnChanges(): void {
    this.rows = buildBarRows(this.steps ?? []);
    this.cdr.markForCheck();
  }

  toggleTable(): void {
    this.showTable = !this.showTable;
    this.cdr.markForCheck();
  }
}
