import { ChangeDetectorRef, Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { AbstractControl, FormBuilder, FormControl, FormGroup, ValidationErrors, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { WorkspaceService } from '../../../core/layout/workspace.service';
import { Campaign, CampaignRequest, DAY_NAMES, daysToSet, setToDays } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { windowSummary } from '../schedule-display';
import { TimeZoneDialog } from '../dialogs/dialogs';

const windowOrder = (g: AbstractControl): ValidationErrors | null => {
  const { windowStart, windowEnd } = g.value as { windowStart: string; windowEnd: string };
  return windowStart && windowEnd && windowStart >= windowEnd ? { windowOrder: true } : null;
};

export function supportedTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  const zones = intl.supportedValuesOf?.('timeZone') ?? [];
  return zones.includes('UTC') ? zones : ['UTC', ...zones];
}

@Component({
  selector: 'app-settings-tab',
  standalone: false,
  templateUrl: './settings-tab.html',
  styleUrl: './settings-tab.css'
})
export class SettingsTab implements OnChanges {
  @Input({ required: true }) campaign!: Campaign;
  @Output() campaignChange = new EventEmitter<Campaign>();

  readonly dayNames = DAY_NAMES;
  readonly zones = supportedTimeZones();
  saving = false;
  error: string | null = null;

  form: FormGroup<{
    name: FormControl<string>;
    fromName: FormControl<string>;
    dailySendCap: FormControl<number>;
    windowStart: FormControl<string>;
    windowEnd: FormControl<string>;
    days: FormControl<number[]>;
    timeZoneId: FormControl<string | null>;
  }>;

  constructor(
    fb: FormBuilder,
    private service: CampaignsService,
    private workspaces: WorkspaceService,
    private dialog: MatDialog,
    private snack: MatSnackBar,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.group({
      name: fb.nonNullable.control('', [Validators.required, Validators.maxLength(200), Validators.pattern(/\S/)]),
      fromName: fb.nonNullable.control('', [Validators.maxLength(100)]),
      dailySendCap: fb.nonNullable.control(30, [Validators.required, Validators.min(1), Validators.max(500)]),
      windowStart: fb.nonNullable.control('09:00', [Validators.required]),
      windowEnd: fb.nonNullable.control('17:00', [Validators.required]),
      days: fb.nonNullable.control<number[]>([], [Validators.required, Validators.minLength(1)]),
      timeZoneId: fb.control<string | null>(null)
    }, { validators: windowOrder });
  }

  ngOnChanges(changes: SimpleChanges): void {
    const prev = changes['campaign']?.previousValue as Campaign | undefined;
    if (!prev || prev.id !== this.campaign.id || !this.form.dirty) this.reset();
    if (this.campaign.status === 'Completed') this.form.disable({ emitEvent: false }); else this.form.enable({ emitEvent: false });
  }

  reset(): void {
    const c = this.campaign;
    this.form.reset({
      name: c.name, fromName: c.fromName ?? '', dailySendCap: c.dailySendCap, windowStart: c.windowStart, windowEnd: c.windowEnd,
      days: [...daysToSet(c.sendDays)], timeZoneId: c.timeZoneId
    });
  }

  get preview(): string {
    const v = this.form.getRawValue();
    return windowSummary(v.windowStart, v.windowEnd, setToDays(v.days), v.timeZoneId ?? this.campaign.workspaceTimeZone);
  }

  save(): void {
    if (this.form.invalid || this.saving) return;
    const v = this.form.getRawValue();
    const request: CampaignRequest = {
      name: v.name.trim(), fromName: v.fromName.trim() || null, dailySendCap: v.dailySendCap,
      windowStart: v.windowStart, windowEnd: v.windowEnd, sendDays: setToDays(v.days), timeZoneId: v.timeZoneId || null
    };
    this.saving = true;
    this.error = null;
    this.service.update(this.campaign.id, request).subscribe({
      next: c => {
        this.saving = false;
        this.form.markAsPristine();
        this.campaignChange.emit(c);
        this.snack.open('Settings saved.', undefined, { duration: 3000 });
      },
      error: e => {
        this.saving = false;
        this.error = e.status === 409 ? 'A completed campaign can\'t be edited.' : e.status === 400 ? 'Some settings are invalid.' : 'Could not save the settings.';
        this.cdr.markForCheck();
      }
    });
  }

  changeWorkspaceZone(): void {
    this.dialog.open(TimeZoneDialog, { data: { current: this.campaign.workspaceTimeZone, zones: this.zones }, width: '420px' })
      .afterClosed().subscribe((zone?: string) => {
        if (!zone || zone === this.campaign.workspaceTimeZone) return;
        this.workspaces.updateTimeZone(zone).subscribe({
          next: () => this.service.get(this.campaign.id).subscribe(c => this.campaignChange.emit(c)),
          error: () => this.snack.open('Could not change the workspace time zone.', 'OK', { duration: 5000 })
        });
      });
  }
}
