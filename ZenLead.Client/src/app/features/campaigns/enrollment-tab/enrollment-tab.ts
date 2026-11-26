import { ChangeDetectorRef, Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { Router } from '@angular/router';
import { PageEvent } from '@angular/material/paginator';
import { Campaign, ENROLLMENT_STATUSES, EnrollmentRow, EnrollmentStatus } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { formatInZone, statusChip } from '../format';

@Component({
  selector: 'app-enrollment-tab',
  standalone: false,
  templateUrl: './enrollment-tab.html',
  styleUrl: './enrollment-tab.css'
})
export class EnrollmentTab implements OnChanges {
  @Input({ required: true }) campaign!: Campaign;

  readonly columns = ['lead', 'email', 'step', 'nextSend', 'status', 'reason'];
  readonly statuses = ENROLLMENT_STATUSES;
  readonly chip = statusChip;
  status: EnrollmentStatus | null = null;
  page = 1;
  pageSize = 25;
  rows: EnrollmentRow[] = [];
  total = 0;
  loading = false;
  error = false;

  constructor(private service: CampaignsService, private router: Router, private cdr: ChangeDetectorRef) {}

  ngOnChanges(changes: SimpleChanges): void {
    const prev = changes['campaign']?.previousValue as Campaign | undefined;
    // reload on first show and when the campaign's state changes (activate/pause re-stamps next sends)
    if (!prev || prev.id !== this.campaign.id || prev.status !== this.campaign.status) this.load();
  }

  get counts() {
    const c = this.campaign.counts;
    return [
      { label: 'Active', value: c.active }, { label: 'Completed', value: c.completed }, { label: 'Replied', value: c.replied },
      { label: 'Unsubscribed', value: c.unsubscribed }, { label: 'Bounced', value: c.bounced }, { label: 'Failed', value: c.failed }
    ];
  }

  load(): void {
    this.loading = true;
    this.error = false;
    this.service.enrollments(this.campaign.id, this.status, this.page, this.pageSize).subscribe({
      next: r => { this.rows = r.items; this.total = r.total; this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.error = true; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  onStatus(status: EnrollmentStatus | null): void { this.status = status; this.page = 1; this.load(); }

  onPage(e: PageEvent): void { this.page = e.pageIndex + 1; this.pageSize = e.pageSize; this.load(); }

  addLeads(): void { this.router.navigate(['/leads'], { queryParams: { enrollIn: this.campaign.id } }); }

  stepLabel(row: EnrollmentRow): string {
    const total = this.campaign.steps.length;
    if (row.status === 'Completed') return `All ${total} sent`;
    if (row.status !== 'Active' && row.status !== 'Paused') return '—';
    return `Step ${Math.min(row.nextStepOrder, Math.max(total, 1))} of ${total}`;
  }

  nextSend(row: EnrollmentRow): string {
    if (row.status !== 'Active' && row.status !== 'Paused') return '—';
    if (this.campaign.status === 'Paused' || row.status === 'Paused') return 'Paused';
    return row.nextSendAt ? formatInZone(row.nextSendAt, this.campaign.effectiveTimeZone) : '—';
  }
}
