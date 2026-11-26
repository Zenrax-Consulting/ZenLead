import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import { ActivationPreview, ActivationProblem, Campaign, CampaignConflict, SenderStatus } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { statusChip } from '../format';
import { timelineSummary, windowSummary } from '../schedule-display';
import {
  ActivationConfirmData, ActivationConfirmDialog, ActivationProblemsDialog, ProblemsResult, SenderHelpDialog
} from '../dialogs/dialogs';

export const TABS = ['steps', 'leads', 'settings'] as const;
export type TabName = typeof TABS[number];

/** Ask the steps tab to bring a step into view (a fresh object each time, so asking twice for the same step still works). */
export interface FocusStepRequest { order: number; }

@Component({
  selector: 'app-campaign-detail',
  standalone: false,
  templateUrl: './campaign-detail.html',
  styleUrl: './campaign-detail.css'
})
export class CampaignDetail implements OnInit, OnDestroy {
  campaign: Campaign | null = null;
  sender: SenderStatus | null = null;
  loadError = false;
  busy = false;
  selectedIndex = 0;
  focusStep: FocusStepRequest | null = null;
  readonly chip = statusChip;

  private id = '';
  private subs = new Subscription();

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private service: CampaignsService,
    private dialog: MatDialog,
    private snack: MatSnackBar,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.subs.add(this.route.paramMap.subscribe(p => {
      const id = p.get('id')!;
      if (id !== this.id) { this.id = id; this.load(); }
    }));
    this.subs.add(this.route.queryParamMap.subscribe(q => {
      const index = TABS.indexOf((q.get('tab') ?? 'steps') as TabName);
      this.selectedIndex = index < 0 ? 0 : index;
      this.cdr.markForCheck();
    }));
    this.service.senderStatus().subscribe({ next: s => { this.sender = s; this.cdr.markForCheck(); }, error: () => {} });
  }

  ngOnDestroy(): void { this.subs.unsubscribe(); }

  load(): void {
    this.loadError = false;
    this.service.get(this.id).subscribe({
      next: c => { this.campaign = c; this.cdr.markForCheck(); },
      error: () => { this.loadError = true; this.cdr.markForCheck(); }
    });
  }

  onCampaignChange(c: Campaign): void { this.campaign = c; this.cdr.markForCheck(); }

  onTabChange(index: number): void {
    this.selectedIndex = index;
    this.router.navigate([], { relativeTo: this.route, queryParams: { tab: index === 0 ? null : TABS[index] }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  get timeline(): string { return this.campaign ? timelineSummary(this.campaign.steps) : ''; }

  get window(): string {
    const c = this.campaign;
    return c ? windowSummary(c.windowStart, c.windowEnd, c.sendDays, c.effectiveTimeZone) : '';
  }

  get canActivate(): boolean { return this.campaign?.status === 'Draft' || this.campaign?.status === 'Paused'; }
  get canPause(): boolean { return this.campaign?.status === 'Active'; }

  openSenderHelp(): void { this.dialog.open(SenderHelpDialog, { data: this.sender, width: '560px' }); }

  activate(): void {
    const c = this.campaign;
    if (!c || this.busy) return;
    this.busy = true;
    this.service.activationPreview(c.id).subscribe({
      next: preview => this.confirmActivation(c, preview),
      error: () => { this.busy = false; this.snack.open('Could not prepare activation.', 'OK', { duration: 5000 }); this.cdr.markForCheck(); }
    });
  }

  private confirmActivation(c: Campaign, preview: ActivationPreview): void {
    const data: ActivationConfirmData = { campaignName: c.name, preview, timeline: this.timeline, window: this.window };
    this.dialog.open(ActivationConfirmDialog, { data, width: '520px' }).afterClosed().subscribe(ok => {
      if (!ok) { this.busy = false; this.cdr.markForCheck(); return; }
      this.service.activate(c.id).subscribe({
        next: updated => {
          this.busy = false;
          this.campaign = updated;
          this.snack.open('Campaign activated.', undefined, { duration: 4000 });
          this.cdr.markForCheck();
        },
        error: e => { this.busy = false; this.onActivateError(e); this.cdr.markForCheck(); }
      });
    });
  }

  private onActivateError(e: { status?: number; error?: CampaignConflict }): void {
    const problems = e.status === 409 ? e.error?.problems ?? [] : [];
    if (problems.length) { this.showProblems(problems); return; }
    this.snack.open(e.status === 409 ? (e.error?.message ?? 'The campaign can\'t be activated.') : 'Could not activate the campaign.', 'OK', { duration: 6000 });
  }

  showProblems(problems: ActivationProblem[]): void {
    this.dialog.open(ActivationProblemsDialog, { data: problems, width: '520px' }).afterClosed().subscribe((r?: ProblemsResult) => {
      if (!r) return;
      if (r.action === 'sender-help') this.openSenderHelp();
      else this.fixStep(r.stepOrder);
    });
  }

  /** Select the Steps tab and bring the step into view (the steps tab scrolls to and focuses it). */
  fixStep(order: number): void {
    this.onTabChange(0);
    this.focusStep = { order };
    this.cdr.markForCheck();
  }

  pause(): void {
    const c = this.campaign;
    if (!c || this.busy) return;
    this.busy = true;
    this.service.pause(c.id).subscribe({
      next: updated => { this.busy = false; this.campaign = updated; this.cdr.markForCheck(); },
      error: () => { this.busy = false; this.snack.open('Could not pause the campaign.', 'OK', { duration: 5000 }); this.cdr.markForCheck(); }
    });
  }
}
