import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { AnalyticsTotals, CampaignRow, WorkspaceAnalytics } from '../analytics.models';
import { AnalyticsService } from '../analytics.service';
import { toParams } from '../date-range';
import { AnalyticsDashboard, campaignSortValue } from './analytics-dashboard';

const totals = (over: Partial<AnalyticsTotals> = {}): AnalyticsTotals => ({
  sent: 10, delivered: 9, opened: 4, bounced: 1, contactedLeads: 10, repliedLeads: 2,
  deliveredRate: 0.9, openRate: 0.44, replyRate: 0.2, bounceRate: 0.1, opensApproximate: true, ...over
});

const summary = (campaigns: CampaignRow[] = []): WorkspaceAnalytics =>
  ({ from: '2026-02-10', to: '2026-03-11', timeZone: 'Asia/Dubai', totals: totals(), trend: [], campaigns });

describe('AnalyticsDashboard', () => {
  let workspace: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.fn>;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  const build = (params: Record<string, string> = {}, response: () => ReturnType<AnalyticsService['workspace']> = () => of(summary())) => {
    workspace = vi.fn(response);
    navigate = vi.fn();
    query = new BehaviorSubject(convertToParamMap(params));
    const route = { queryParamMap: query } as unknown as ActivatedRoute;
    const page = new AnalyticsDashboard(route, { navigate } as unknown as Router, { workspace } as unknown as AnalyticsService,
      { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    page.ngOnInit();
    return page;
  };

  it('loads the range from the URL and builds the tiles', () => {
    const page = build({ from: '2026-01-01', to: '2026-01-31' });
    expect(workspace).toHaveBeenCalledWith({ from: '2026-01-01', to: '2026-01-31' });
    expect(page.tiles.map(t => t.label)).toEqual(['Sent', 'Delivered', 'Opened ≈', 'Replied', 'Bounced']);
    expect(page.loading).toBe(false);
  });

  it('defaults to the last 30 days', () => {
    build();
    expect(workspace).toHaveBeenCalledWith(toParams({ kind: 'preset', preset: '30d' }));
  });

  it('a range change writes query params, and the new params trigger a request with from/to', () => {
    const page = build();
    page.onRangeChange({ kind: 'preset', preset: '7d' });
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { range: '7d', from: null, to: null }, queryParamsHandling: 'merge' }));

    query.next(convertToParamMap({ range: '7d' }));
    expect(workspace).toHaveBeenLastCalledWith(toParams({ kind: 'preset', preset: '7d' }));
    const last = workspace.mock.calls.at(-1)![0];
    expect(last.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(last.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('shows an error and retries', () => {
    let fail = true;
    const page = build({}, () => (fail ? throwError(() => ({ status: 500 })) : of(summary())));
    expect(page.errorMessage).toContain('Could not load analytics');
    expect(page.loading).toBe(false);

    fail = false;
    page.retry();
    expect(workspace).toHaveBeenCalledTimes(2);
    expect(page.errorMessage).toBeNull();
    expect(page.data).not.toBeNull();
  });

  it('shows the server message for an invalid range', () => {
    const page = build({}, () => throwError(() => ({ status: 400, error: { detail: 'The range can be at most 366 days.' } })));
    expect(page.errorMessage).toBe('The range can be at most 366 days.');
  });

  it('fills the campaigns table and sorts by rate with missing rates last', () => {
    const rows: CampaignRow[] = [
      { campaignId: 'a', name: 'Alpha', status: 'Active', totals: totals({ openRate: 0.5 }) },
      { campaignId: 'b', name: 'beta', status: 'Completed', totals: totals({ openRate: null }) }
    ];
    const page = build({}, () => of(summary(rows)));
    expect(page.table.data.length).toBe(2);
    expect(campaignSortValue(rows[0], 'opened')).toBe(0.5);
    expect(campaignSortValue(rows[1], 'opened')).toBe(-1);
    expect(campaignSortValue(rows[1], 'name')).toBe('beta');
  });
});
