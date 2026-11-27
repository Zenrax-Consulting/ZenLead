import { ChangeDetectionStrategy, ChangeDetectorRef, Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { FormControl, FormGroup } from '@angular/forms';
import { PRESETS, RangePreset, RangeSelection, parseIsoDate, toIsoDate, validateRange } from '../date-range';

/** Preset toggle (Last 7 / 30 / 90 days) plus a custom date range. Emits a new selection; the page keeps it in the URL. */
@Component({
  selector: 'app-range-bar',
  standalone: false,
  templateUrl: './range-bar.html',
  styleUrl: './range-bar.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RangeBar implements OnChanges {
  @Input({ required: true }) selection!: RangeSelection;
  @Input() timeZone: string | null = null;
  @Output() selectionChange = new EventEmitter<RangeSelection>();

  readonly presets = PRESETS;
  custom = false;
  error: string | null = null;
  readonly form = new FormGroup({ start: new FormControl<Date | null>(null), end: new FormControl<Date | null>(null) });

  constructor(private cdr: ChangeDetectorRef) {}

  get toggleValue(): string { return this.custom ? 'custom' : this.selection.kind === 'preset' ? this.selection.preset : 'custom'; }

  ngOnChanges(): void {
    this.custom = this.selection.kind === 'custom';
    if (this.selection.kind === 'custom')
      this.form.setValue({ start: parseIsoDate(this.selection.from), end: parseIsoDate(this.selection.to) }, { emitEvent: false });
    this.error = null;
    this.cdr.markForCheck();
  }

  onToggle(value: string): void {
    if (value === 'custom') {
      this.custom = true;
      this.cdr.markForCheck();
      return;
    }
    this.custom = false;
    this.selectionChange.emit({ kind: 'preset', preset: value as RangePreset });
  }

  /** Called when the date range picker closes or a date is typed; only a complete, valid range is emitted. */
  applyCustom(): void {
    const { start, end } = this.form.getRawValue();
    if (!start || !end) return;
    const from = toIsoDate(start), to = toIsoDate(end);
    this.error = validateRange(from, to);
    this.cdr.markForCheck();
    if (this.error === null) this.selectionChange.emit({ kind: 'custom', from, to });
  }
}
