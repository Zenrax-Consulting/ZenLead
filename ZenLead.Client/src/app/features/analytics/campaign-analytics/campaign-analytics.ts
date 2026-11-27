import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription, combineLatest } from 'rxjs';
import { statusChip } from '../../campaigns/format';
import { CampaignAnalytics as CampaignAnalyticsData } from '../analytics.models';
import { AnalyticsService } from '../analytics.service';
import { RangeSelection, parseSelection, selectionToQuery, toParams } from '../date-range';
import { StatTileModel, buildTiles } from '../tiles';

/** /analytics/campaigns/:id — the campaign's tiles, trend and per-step breakdown for the selected range. */
@Component({
  selector: 'app-campaign-analytics',
  standalone: false,
  templateUrl: './campaign-analytics.html',
  styleUrl: './campaign-analytics.css'
})
export class CampaignAnalytics implements OnInit, OnDestroy {
  readonly chip = statusChip;

  campaignId = '';
  selection: RangeSelection = { kind: 'preset', preset: '30d' };
  data: CampaignAnalyticsData | null = null;
  tiles: StatTileModel[] = [];
  loading = true;
  notFound = false;
  errorMessage: string | null = null;

  private subs = new Subscription();
  private loadSub?: Subscription;

  constructor(private route: ActivatedRoute, private router: Router, private analytics: AnalyticsService, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.subs.add(combineLatest([this.route.paramMap, this.route.queryParamMap]).subscribe(([params, query]) => {
      this.campaignId = params.get('id') ?? '';
      this.selection = parseSelection(query);
      this.load();
    }));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.loadSub?.unsubscribe();
  }

  get isDraft(): boolean { return this.data?.status === 'Draft'; }

  onRangeChange(selection: RangeSelection): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: selectionToQuery(selection), queryParamsHandling: 'merge' });
  }

  retry(): void { this.load(); }

  load(): void {
    this.loadSub?.unsubscribe();
    this.loading = true;
    this.errorMessage = null;
    this.notFound = false;
    this.cdr.markForCheck();
    this.loadSub = this.analytics.campaign(this.campaignId, toParams(this.selection)).subscribe({
      next: data => {
        this.data = { ...data, steps: [...data.steps].sort((a, b) => (a.order || Number.MAX_SAFE_INTEGER) - (b.order || Number.MAX_SAFE_INTEGER)) };
        this.tiles = buildTiles(data.totals);
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: err => {
        this.loading = false;
        if (err?.status === 404) {
          this.notFound = true;
          this.data = null;
        } else {
          this.errorMessage = err?.status === 400 && err?.error?.detail ? err.error.detail : 'Could not load campaign analytics. Check your connection and try again.';
        }
        this.cdr.markForCheck();
      }
    });
  }
}
