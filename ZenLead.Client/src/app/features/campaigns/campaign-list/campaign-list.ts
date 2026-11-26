import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { CampaignListItem, defaultCampaignRequest } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { statusChip } from '../format';
import { NewCampaignDialog } from '../dialogs/dialogs';

@Component({
  selector: 'app-campaign-list',
  standalone: false,
  templateUrl: './campaign-list.html',
  styleUrl: './campaign-list.css'
})
export class CampaignList implements OnInit {
  readonly columns = ['name', 'status', 'steps', 'enrolled', 'sent', 'createdAt'];
  readonly chip = statusChip;
  campaigns: CampaignListItem[] | null = null;
  loading = true;
  error: string | null = null;
  creating = false;

  constructor(private service: CampaignsService, private router: Router, private dialog: MatDialog, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = null;
    this.service.list().subscribe({
      next: list => { this.campaigns = list; this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.error = 'Failed to load campaigns.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  open(row: CampaignListItem): void { this.router.navigate(['/campaigns', row.id]); }

  newCampaign(): void {
    this.dialog.open(NewCampaignDialog, { width: '420px' }).afterClosed().subscribe((name?: string) => {
      if (!name) return;
      this.creating = true;
      this.service.create(defaultCampaignRequest(name)).subscribe({
        next: c => { this.creating = false; this.router.navigate(['/campaigns', c.id]); },
        error: () => { this.creating = false; this.error = 'Could not create the campaign.'; this.cdr.markForCheck(); }
      });
    });
  }
}
