import { ChangeDetectorRef, Component, Inject, OnDestroy } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { Observable, Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { Credits, DiscoveryRun, TargetProfile, isTerminal } from '../profiles.models';
import { ProfilesService } from '../profiles.service';

export interface RunDiscoveryDialogData {
  profile: TargetProfile;
  credits: Credits;
}

export const POLL_INTERVAL_MS = 2000;

/** Emits the run immediately and then every 2 s, completing after the first terminal state (which is emitted). */
export function pollRun(service: Pick<ProfilesService, 'getRun'>, id: string, intervalMs = POLL_INTERVAL_MS): Observable<DiscoveryRun> {
  return timer(0, intervalMs).pipe(
    switchMap(() => service.getRun(id)),
    takeWhile(run => !isTerminal(run), true));
}

@Component({
  selector: 'app-run-discovery-dialog',
  standalone: false,
  templateUrl: './run-discovery-dialog.html',
  styleUrl: './run-discovery-dialog.css'
})
export class RunDiscoveryDialog implements OnDestroy {
  readonly maxControl: FormControl<number>;
  run: DiscoveryRun | null = null;
  starting = false;
  errorMessage: string | null = null;
  private pollSub?: Subscription;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: RunDiscoveryDialogData,
    private dialogRef: MatDialogRef<RunDiscoveryDialog>,
    private profiles: ProfilesService,
    private router: Router,
    private cdr: ChangeDetectorRef
  ) {
    const max = data.credits.maxLeadsPerRun;
    this.maxControl = new FormControl(Math.min(25, max), {
      nonNullable: true, validators: [Validators.required, Validators.min(1), Validators.max(max)]
    });
  }

  ngOnDestroy(): void { this.pollSub?.unsubscribe(); }

  get inProgress(): boolean { return this.run !== null && !isTerminal(this.run); }
  get finished(): boolean { return this.run !== null && isTerminal(this.run); }
  get capped(): boolean { return this.data.credits.remaining <= 0; }

  get statusLabel(): string {
    switch (this.run?.status) {
      case 'Completed': return 'Completed';
      case 'CapReached': return 'Cap reached';
      case 'Failed': return `Failed — ${this.run.failureReason ?? 'unknown error'}`;
      case 'Running': return 'Running';
      default: return 'Queued';
    }
  }

  start(): void {
    if (this.maxControl.invalid || this.starting || this.capped) return;
    this.starting = true;
    this.errorMessage = null;
    this.profiles.startRun(this.data.profile.id, this.maxControl.value).subscribe({
      next: run => { this.starting = false; this.run = run; this.poll(run.id); this.cdr.markForCheck(); },
      error: err => {
        this.starting = false;
        this.errorMessage = err.status === 409 ? (err.error?.message ?? 'A run is already in progress.')
          : err.status === 429 ? 'Too many runs started recently. Wait a minute and try again.'
          : 'Failed to start the run.';
        this.cdr.markForCheck();
      }
    });
  }

  private poll(id: string): void {
    this.pollSub?.unsubscribe();
    this.pollSub = pollRun(this.profiles, id).subscribe({
      next: run => { this.run = run; this.cdr.markForCheck(); },
      error: () => { this.errorMessage = 'Lost contact with the server while checking progress.'; this.cdr.markForCheck(); }
    });
  }

  viewImported(): void {
    if (!this.run) return;
    this.dialogRef.close(this.run);
    this.router.navigate(['/leads'], { queryParams: { sourceRunId: this.run.id } });
  }
}
