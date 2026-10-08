import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { forkJoin } from 'rxjs';
import { ConfirmDialog } from '../../confirm-dialog/confirm-dialog';
import { Credits, DiscoveryRun, TargetProfile, criteriaChips } from '../profiles.models';
import { ProfilesService } from '../profiles.service';
import { RunDiscoveryDialog } from '../run-discovery-dialog/run-discovery-dialog';

/** Fraction of the monthly cap at which the meter switches to its warning style (and text). */
export const WARN_AT = 0.9;

@Component({
  selector: 'app-target-profiles-list',
  standalone: false,
  templateUrl: './target-profiles-list.html',
  styleUrl: './target-profiles-list.css'
})
export class TargetProfilesList implements OnInit {
  readonly columns = ['name', 'criteria', 'createdAt', 'actions'];
  profiles: TargetProfile[] = [];
  runs: DiscoveryRun[] = [];
  credits: Credits | null = null;
  loading = true;
  errorMessage: string | null = null;
  actionError: string | null = null;

  constructor(private service: ProfilesService, private dialog: MatDialog, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.errorMessage = null;
    forkJoin({ profiles: this.service.list(), credits: this.service.credits(), runs: this.service.listRuns() }).subscribe({
      next: r => { this.profiles = r.profiles; this.credits = r.credits; this.runs = r.runs; this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.errorMessage = 'Failed to load target profiles.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  chips = criteriaChips;

  get usedPercent(): number {
    return this.credits && this.credits.cap > 0 ? Math.min(100, Math.round((this.credits.used / this.credits.cap) * 100)) : 0;
  }
  get nearCap(): boolean { return !!this.credits && this.credits.cap > 0 && this.credits.used / this.credits.cap >= WARN_AT; }

  profileName(run: DiscoveryRun): string {
    return this.profiles.find(p => p.id === run.targetProfileId)?.name ?? 'Deleted profile';
  }

  run(profile: TargetProfile): void {
    if (!this.credits) return;
    this.dialog.open(RunDiscoveryDialog, { width: '440px', data: { profile, credits: this.credits } })
      .afterClosed().subscribe(() => this.load());
  }

  confirmDelete(profile: TargetProfile): void {
    this.dialog.open(ConfirmDialog, {
      data: { title: 'Delete target profile?', message: `"${profile.name}" will be removed. Leads it already found are kept.`, confirmLabel: 'Delete' }
    }).afterClosed().subscribe(confirmed => {
      if (!confirmed) return;
      this.actionError = null;
      this.service.delete(profile.id).subscribe({
        next: () => this.load(),
        error: () => { this.actionError = 'Failed to delete the profile.'; this.cdr.markForCheck(); }
      });
    });
  }
}
