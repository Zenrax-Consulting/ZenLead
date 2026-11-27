import { convertToParamMap } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { formatCount, formatRate } from './format-rate';
import { daysInclusive, parseIsoDate, parseSelection, selectionToQuery, toIsoDate, toParams, validateRange } from './date-range';
import { labelIndices, niceScale, separateLabels, xPositions, yPosition } from './chart-scales';
import { AnalyticsService } from './analytics.service';
import { buildBarRows } from './bar-list/bar-list';
import { StepBreakdown } from './analytics.models';

describe('formatRate', () => {
  it('shows a dash for missing or non-finite rates, never NaN%', () => {
    expect(formatRate(null)).toBe('—');
    expect(formatRate(undefined)).toBe('—');
    expect(formatRate(NaN)).toBe('—');
    expect(formatRate(Infinity)).toBe('—');
  });

  it('rounds to one decimal and keeps 0 %', () => {
    expect(formatRate(0.4286)).toBe('42.9%');
    expect(formatRate(0)).toBe('0.0%');
    expect(formatRate(1)).toBe('100.0%');
    expect(formatRate(0.00049)).toBe('0.0%');
  });

  it('clamps to 0–100 %', () => {
    expect(formatRate(1.2)).toBe('100.0%');
    expect(formatRate(-0.1)).toBe('0.0%');
  });

  it('formats counts with separators', () => expect(formatCount(12345)).toBe('12,345'));
});

describe('date-range', () => {
  const today = new Date(2026, 2, 11);   // 11 March 2026, local

  it('presets end today and include it', () => {
    expect(toParams({ kind: 'preset', preset: '7d' }, today)).toEqual({ from: '2026-03-05', to: '2026-03-11' });
    expect(toParams({ kind: 'preset', preset: '30d' }, today)).toEqual({ from: '2026-02-10', to: '2026-03-11' });
    expect(daysInclusive('2026-02-10', '2026-03-11')).toBe(30);
  });

  it('serialises custom ranges as yyyy-MM-dd', () => {
    expect(toParams({ kind: 'custom', from: '2026-01-01', to: '2026-01-31' })).toEqual({ from: '2026-01-01', to: '2026-01-31' });
    expect(toIsoDate(new Date(2026, 0, 5))).toBe('2026-01-05');
  });

  it('validates from <= to and at most 366 days', () => {
    expect(validateRange('2026-03-02', '2026-03-01')).toContain('on or before');
    expect(validateRange('2025-01-01', '2026-01-02')).toContain('366');
    expect(validateRange('2025-01-01', '2026-01-01')).toBeNull();
    expect(validateRange('2026-02-30', '2026-03-01')).toContain('Choose');
    expect(validateRange(null, '2026-03-01')).toContain('Choose');
  });

  it('counts days across a DST change correctly', () => expect(daysInclusive('2026-03-05', '2026-03-11')).toBe(7));

  it('reads and writes the URL form', () => {
    expect(parseSelection(convertToParamMap({ range: '90d' }))).toEqual({ kind: 'preset', preset: '90d' });
    expect(parseSelection(convertToParamMap({ range: 'bogus' }))).toEqual({ kind: 'preset', preset: '30d' });
    expect(parseSelection(convertToParamMap({ from: '2026-01-01', to: '2026-01-31' }))).toEqual({ kind: 'custom', from: '2026-01-01', to: '2026-01-31' });
    expect(parseSelection(convertToParamMap({ from: '2026-02-01', to: '2026-01-01' }))).toEqual({ kind: 'preset', preset: '30d' });
    expect(selectionToQuery({ kind: 'preset', preset: '7d' })).toEqual({ range: '7d', from: null, to: null });
    expect(selectionToQuery({ kind: 'custom', from: 'a', to: 'b' })).toEqual({ range: null, from: 'a', to: 'b' });
    expect(parseIsoDate('2026-13-01')).toBeNull();
  });
});

describe('chart-scales', () => {
  it('nice max and integer ticks for counts', () => {
    expect(niceScale(0)).toEqual({ max: 1, step: 1, ticks: [0, 1] });
    expect(niceScale(3)).toEqual({ max: 3, step: 1, ticks: [0, 1, 2, 3] });
    const s = niceScale(37);
    expect(s.max).toBe(40);
    expect(s.ticks).toEqual([0, 10, 20, 30, 40]);
    expect(niceScale(230).ticks.at(-1)).toBeGreaterThanOrEqual(230);
    expect(niceScale(7).ticks.every(Number.isInteger)).toBe(true);
  });

  it('spreads N points across the plot', () => {
    expect(xPositions(0, 0, 100)).toEqual([]);
    expect(xPositions(1, 0, 100)).toEqual([50]);
    expect(xPositions(3, 0, 100)).toEqual([0, 50, 100]);
  });

  it('maps values to y with 0 at the bottom', () => {
    expect(yPosition(0, 10, 0, 100)).toBe(100);
    expect(yPosition(10, 10, 0, 100)).toBe(0);
    expect(yPosition(5, 0, 0, 100)).toBe(100);
  });

  it('thins x labels but keeps the first and last', () => {
    expect(labelIndices(5, 6)).toEqual([0, 1, 2, 3, 4]);
    const many = labelIndices(90, 6);
    expect(many[0]).toBe(0);
    expect(many.at(-1)).toBe(89);
    expect(many.length).toBeLessThanOrEqual(6);
  });

  it('separates colliding end labels', () => {
    const ys = separateLabels([100, 102, 101], 16, 0, 200);
    const sorted = [...ys].sort((a, b) => a - b);
    expect(sorted[1] - sorted[0]).toBeGreaterThanOrEqual(16);
    expect(sorted[2] - sorted[1]).toBeGreaterThanOrEqual(16);
  });
});

describe('buildBarRows', () => {
  const step = (order: number, sent: number, replies = 0): StepBreakdown =>
    ({ stepId: `s${order}`, order, subjectTemplate: `Subject ${order}`, sent, delivered: sent, opened: 0, bounced: 0, replies });

  it('orders by step order (removed steps last) and shares one scale', () => {
    const rows = buildBarRows([step(2, 5), step(0, 1), step(1, 10, 2)]);
    expect(rows.map(r => r.step.order)).toEqual([1, 2, 0]);
    expect(rows[0].title).toBe('Step 1 · Subject 1');
    expect(rows[0].bars[0]).toEqual({ label: 'Sent', value: 10, pct: 100 });
    expect(rows[1].bars[0].pct).toBe(50);
    expect(rows[0].bars.map(b => b.label)).toEqual(['Sent', 'Delivered', 'Opened ≈', 'Replies']);
  });
});

describe('AnalyticsService', () => {
  it('sends from/to to the workspace and campaign summaries', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const service = TestBed.inject(AnalyticsService);
    const http = TestBed.inject(HttpTestingController);

    service.workspace({ from: '2026-03-01', to: '2026-03-10' }).subscribe();
    const w = http.expectOne(r => r.url === '/api/v1/analytics/workspace/summary');
    expect(w.request.params.toString()).toBe('from=2026-03-01&to=2026-03-10');
    w.flush({});

    service.campaign('c1', { from: '2026-03-01', to: '2026-03-10' }).subscribe();
    http.expectOne(r => r.url === '/api/v1/analytics/campaigns/c1/summary').flush({});
    http.verify();
  });
});
