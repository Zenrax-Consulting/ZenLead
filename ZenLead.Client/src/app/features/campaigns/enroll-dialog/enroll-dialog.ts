import { ChangeDetectorRef, Component, Inject, OnInit } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { LeadQuery } from '../../leads/leads.models';
import { EnrollResult, EnrollTarget } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';

export interface EnrollDialogData {
  campaignId: string;
  campaignName: string;
  leadIds?: string[];
  filter?: LeadQuery;
  /** How many leads the user picked (ids) or the filter matched. */
  total: number;
}

/** Dry-run on open so the user sees exactly what will be added and skipped; Confirm repeats it for real. */
@Component({
  selector: 'app-enroll-dialog',
  standalone: false,
  templateUrl: './enroll-dialog.html',
  styleUrl: './enroll-dialog.css'
})
export class EnrollDialog implements OnInit {
  loading = false;
  submitting = false;
  preview: EnrollResult | null = null;
  error: string | null = null;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: EnrollDialogData,
    private dialogRef: MatDialogRef<EnrollDialog, EnrollResult>,
    private service: CampaignsService,
    private cdr: ChangeDetectorRef
  ) {}

  private target(): EnrollTarget {
    return this.data.leadIds ? { leadIds: this.data.leadIds } : { filter: this.data.filter! };
  }

  ngOnInit(): void {
    this.loading = true;
    this.service.enroll(this.data.campaignId, this.target(), true).subscribe({
      next: r => { this.preview = r; this.loading = false; this.cdr.markForCheck(); },
      error: e => { this.error = this.messageFor(e); this.loading = false; this.cdr.markForCheck(); }
    });
  }

  get skipped(): number {
    const p = this.preview;
    return p ? p.skippedSuppressed + p.skippedInvalidEmail + p.skippedMissingTokens + p.notFound : 0;
  }

  get canConfirm(): boolean { return !!this.preview && this.preview.enrolled > 0 && !this.submitting && !this.loading; }

  confirm(): void {
    if (!this.canConfirm) return;
    this.submitting = true;
    this.error = null;
    this.service.enroll(this.data.campaignId, this.target(), false).subscribe({
      next: r => this.dialogRef.close(r),
      error: e => { this.submitting = false; this.error = this.messageFor(e); this.cdr.markForCheck(); }
    });
  }

  private messageFor(e: { status?: number }): string {
    return e.status === 409 ? 'This campaign is completed.' : 'Could not add leads.';
  }
}
