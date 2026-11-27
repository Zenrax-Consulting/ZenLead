import { ChangeDetectorRef, Component, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { MatSort } from '@angular/material/sort';
import { MatTableDataSource } from '@angular/material/table';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { statusChip } from '../../campaigns/format';
import { CampaignRow, WorkspaceAnalytics } from '../analytics.models';
import { AnalyticsService } from '../analytics.service';
import { RangeSelection, parseSelection, selectionToQuery, toParams } from '../date-range';
import { formatCount, formatRate } from '../format-rate';
import { StatTileModel, buildTiles } from '../tiles';

export const CAMPAIGN_COLUMNS = ['name', 'status', 'sent', 'delivered', 'opened', 'replied', 'bounced'];

/** Sort keys for the campaigns table (client-side; a workspace has at most a few dozen campaigns). */
export function campaignSortValue(row: CampaignRow, column: string): string | number {
  const t = row.totals;
  switch (column) {
    case 'name': return row.name.toLowerCase();
    case 'status': return row.status;
    case 'sent': return t.sent;
    case 'delivered': return t.deliveredRate ?? -1;
    case 'opened': return t.openRate ?? -1;
    case 'replied': return t.replyRate ?? -1;
    case 'bounced': return t.bounceRate ?? -1;
    default: return '';
  }
}

/** /analytics: workspace tiles, daily trend and the campaigns table for the selected range (kept in the query string). */
@Component({
  selector: 'app-analytics-dashboard',
  standalone: false,
  templateUrl: './analytics-dashboard.html',
  styleUrl: './analytics-dashboard.css'
})
export class AnalyticsDashboard implements OnInit, OnDestroy {
  readonly columns = CAMPAIGN_COLUMNS;
  readonly chip = statusChip;
  readonly rate = formatRate;
  readonly count = formatCount;

  selection: RangeSelection = { kind: 'preset', preset: '30d' };
  data: WorkspaceAnalytics | null = null;
  tiles: StatTileModel[] = [];
  loading = true;
  errorMessage: string | null = null;
  readonly table = new MatTableDataSource<CampaignRow>([]);

  private subs = new Subscription();
  private loadSub?: Subscription;

  @ViewChild(MatSort) set sort(sort: MatSort | undefined) {
    if (sort) this.table.sort = sort;
  }

  constructor(private route: ActivatedRoute, private router: Router, private analytics: AnalyticsService, private cdr: ChangeDetectorRef) {
    this.table.sortingDataAccessor = campaignSortValue;
  }

  ngOnInit(): void {
    this.subs.add(this.route.queryParamMap.subscribe(params => {
      this.selection = parseSelection(params);
      this.load();
    }));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.loadSub?.unsubscribe();
  }

  onRangeChange(selection: RangeSelection): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: selectionToQuery(selection), queryParamsHandling: 'merge' });
  }

  retry(): void { this.load(); }

  load(): void {
    this.loadSub?.unsubscribe();
    this.loading = true;
    this.errorMessage = null;
    this.cdr.markForCheck();
    this.loadSub = this.analytics.workspace(toParams(this.selection)).subscribe({
      next: data => {
        this.data = data;
        this.tiles = buildTiles(data.totals);
        this.table.data = data.campaigns;
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: err => {
        this.loading = false;
        this.errorMessage = err?.status === 400 && err?.error?.detail ? err.error.detail : 'Could not load analytics. Check your connection and try again.';
        this.cdr.markForCheck();
      }
    });
  }
}
