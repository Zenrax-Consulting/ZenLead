import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { MatDialogRef } from '@angular/material/dialog';
import { CampaignListItem, defaultCampaignRequest } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';

export interface PickedCampaign { id: string; name: string; }

/** "Add to campaign" from the leads list: pick a Draft/Active/Paused campaign, or create one by name. */
@Component({
  selector: 'app-campaign-picker-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>Add to campaign</h2>
    <mat-dialog-content>
      <mat-progress-bar *ngIf="loading" mode="indeterminate"></mat-progress-bar>
      <p *ngIf="error" class="error" role="alert">{{ error }}</p>
      <mat-nav-list *ngIf="campaigns.length" aria-label="Campaigns">
        <a mat-list-item *ngFor="let c of campaigns" (click)="pick(c)" (keydown.enter)="pick(c)" tabindex="0">
          <span matListItemTitle>{{ c.name }}</span>
          <span matListItemLine>{{ c.status }} · {{ c.stepCount }} step{{ c.stepCount === 1 ? '' : 's' }} · {{ c.counts.total }} enrolled</span>
        </a>
      </mat-nav-list>
      <p *ngIf="!loading && !campaigns.length && !error" class="muted">No open campaigns yet.</p>
      <form class="new" (ngSubmit)="createNew()">
        <mat-form-field appearance="outline">
          <mat-label>New campaign name</mat-label>
          <input matInput [formControl]="name" maxlength="200">
        </mat-form-field>
        <button mat-stroked-button type="submit" [disabled]="name.invalid || creating">New campaign</button>
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button></mat-dialog-actions>`,
  styles: ['.new { display: flex; gap: 8px; align-items: baseline; margin-top: 12px; } .error { color: #b00020; } .muted { opacity: .7; }']
})
export class CampaignPickerDialog implements OnInit {
  campaigns: CampaignListItem[] = [];
  loading = true;
  creating = false;
  error: string | null = null;
  name = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] });

  constructor(private service: CampaignsService, private dialogRef: MatDialogRef<CampaignPickerDialog, PickedCampaign>,
              private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.service.list().subscribe({
      next: list => { this.campaigns = list.filter(c => c.status !== 'Completed'); this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.error = 'Failed to load campaigns.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  pick(c: CampaignListItem): void { this.dialogRef.close({ id: c.id, name: c.name }); }

  createNew(): void {
    if (this.name.invalid || this.creating) return;
    this.creating = true;
    this.service.create(defaultCampaignRequest(this.name.value.trim())).subscribe({
      next: c => this.dialogRef.close({ id: c.id, name: c.name }),
      error: () => { this.creating = false; this.error = 'Could not create the campaign.'; this.cdr.markForCheck(); }
    });
  }
}
