import { ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CdkDragDrop, moveItemInArray } from '@angular/cdk/drag-drop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Campaign, Step } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { buildTimeline } from '../schedule-display';
import { CampaignConfirmDialog } from '../dialogs/dialogs';
import { FocusStepRequest } from '../campaign-detail/campaign-detail';

const NEW_STEP = { subjectTemplate: 'Following up', bodyTemplate: 'Hi {{firstName|there}},\n\n', useAiPersonalisation: false };

@Component({
  selector: 'app-steps-tab',
  standalone: false,
  templateUrl: './steps-tab.html',
  styleUrl: './steps-tab.css'
})
export class StepsTab implements OnChanges {
  @Input({ required: true }) campaign!: Campaign;
  @Input() focusStep: FocusStepRequest | null = null;
  @Output() campaignChange = new EventEmitter<Campaign>();

  steps: Step[] = [];
  adding = false;

  constructor(
    private service: CampaignsService,
    private snack: MatSnackBar,
    private dialog: MatDialog,
    private host: ElementRef<HTMLElement>,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['campaign']) this.steps = [...this.campaign.steps];
    if (changes['focusStep'] && this.focusStep) this.bringIntoView(this.focusStep.order);
  }

  trackById = (_: number, s: Step) => s.id;

  /** Sent steps are pinned: no delete, no drag. */
  locked(index: number): boolean { return this.campaign.stepsStarted > 0 && index < this.campaign.stepsStarted; }

  /** The server refuses any reorder once a step has gone out of a running campaign, so the UI doesn't offer it either. */
  get reorderLocked(): boolean { return this.campaign.status !== 'Draft' && this.campaign.stepsStarted > 0; }

  get completed(): boolean { return this.campaign.status === 'Completed'; }

  dayOf(index: number): number { return buildTimeline(this.steps)[index]?.day ?? 0; }

  drop(event: CdkDragDrop<Step[]>): void {
    if (event.previousIndex === event.currentIndex) return;
    this.reorder(event.previousIndex, event.currentIndex);
  }

  move(index: number, delta: number): void {
    const target = index + delta;
    if (target < 0 || target >= this.steps.length) return;
    this.reorder(index, target);
  }

  private reorder(from: number, to: number): void {
    if (this.reorderLocked || this.locked(from) || this.locked(to)) {
      this.snack.open('Steps that have already been sent can’t be reordered.', 'OK', { duration: 5000 });
      return;
    }
    const next = [...this.steps];
    moveItemInArray(next, from, to);
    this.steps = next;                                                       // optimistic
    this.service.reorderSteps(this.campaign.id, next.map(s => s.id)).subscribe({
      next: steps => this.emitSteps(steps),
      error: e => { this.reload(); this.showError(e); }
    });
  }

  addStep(): void {
    if (this.adding) return;
    this.adding = true;
    this.service.addStep(this.campaign.id, { ...NEW_STEP, delayDays: this.steps.length === 0 ? 0 : 3 }).subscribe({
      next: step => { this.adding = false; this.emitSteps([...this.steps, step]); },
      error: e => { this.adding = false; this.showError(e); this.cdr.markForCheck(); }
    });
  }

  onSaved(step: Step): void { this.emitSteps(this.steps.map(s => s.id === step.id ? step : s)); }

  remove(step: Step, index: number): void {
    if (this.locked(index)) return;
    this.dialog.open(CampaignConfirmDialog, {
      data: { title: `Delete step ${step.order}?`, message: 'This step and its text will be removed.', confirmLabel: 'Delete' }
    }).afterClosed().subscribe(ok => {
      if (!ok) return;
      this.service.deleteStep(this.campaign.id, step.id).subscribe({
        next: () => this.reload(),                                          // the server renumbers and may reset the first delay
        error: e => { this.reload(); this.showError(e); }
      });
    });
  }

  reload(): void {
    this.service.get(this.campaign.id).subscribe({
      next: c => this.campaignChange.emit(c),
      error: () => {}
    });
  }

  private emitSteps(steps: Step[]): void {
    const ordered = [...steps].sort((a, b) => a.order - b.order);
    this.steps = ordered;
    this.campaignChange.emit({ ...this.campaign, steps: ordered });
    this.cdr.markForCheck();
  }

  private showError(e: { status?: number; error?: { message?: string; detail?: string } }): void {
    const message = e.status === 409 ? e.error?.message : e.status === 400 ? e.error?.detail : null;
    this.snack.open(message ?? 'Something went wrong. Please try again.', 'OK', { duration: 6000 });
    this.cdr.markForCheck();
  }

  private bringIntoView(order: number): void {
    // after the tab switch has rendered
    setTimeout(() => {
      const card = this.host.nativeElement.querySelector<HTMLElement>(`[data-step-order="${order}"]`);
      if (!card) return;
      card.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
      card.querySelector<HTMLElement>('input[formcontrolname="subjectTemplate"]')?.focus();
    });
  }
}
