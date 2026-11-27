import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AnalyticsModule } from '../analytics-module';
import { TrendPoint } from '../analytics.models';
import { TrendChart, describeTrend, shortDay } from './trend-chart';

const points = (n: number, f: (i: number) => Partial<TrendPoint> = i => ({ sent: i + 1, opened: i, replies: i % 2 })): TrendPoint[] =>
  Array.from({ length: n }, (_, i) => ({ day: `2026-03-${String(i + 1).padStart(2, '0')}`, sent: 0, opened: 0, replies: 0, ...f(i) }));

describe('TrendChart', () => {
  let fixture: ComponentFixture<TrendChart>;

  const render = async (data: TrendPoint[]) => {
    await TestBed.configureTestingModule({ imports: [AnalyticsModule] }).compileComponents();
    fixture = TestBed.createComponent(TrendChart);
    fixture.componentRef.setInput('points', data);
    fixture.componentRef.setInput('heading', 'Daily activity');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };
  const refresh = () => { fixture.componentRef.changeDetectorRef.markForCheck(); fixture.detectChanges(); };

  it('renders one marker per point per series, end labels and a legend', async () => {
    const el = await render(points(7));
    for (const key of ['sent', 'opened', 'replies'])
      expect(el.querySelectorAll(`g.series[data-series="${key}"] path.marker`).length).toBe(7);
    const labels = Array.from(el.querySelectorAll('text.end-label')).map(t => t.textContent!.trim());
    expect(labels).toEqual(['Sent 7', 'Opened ≈ 6', 'Replies 0']);
    expect(el.querySelector('.legend')!.textContent).toContain('Opened ≈');
  });

  it('long ranges keep lines but only end markers', async () => {
    const el = await render(points(90, i => ({ sent: i % 5 })));
    expect(el.querySelectorAll('g.series[data-series="sent"] path.marker').length).toBe(1);
  });

  it('is an image with a title and a text description', async () => {
    const el = await render(points(3));
    const svg = el.querySelector('svg[role="img"]')!;
    expect(svg).not.toBeNull();
    expect(svg.querySelector('title')!.textContent).toBe('Daily activity');
    expect(svg.querySelector('desc')!.textContent).toContain('6 sent');
    expect(svg.getAttribute('aria-labelledby')).toContain('-desc');
  });

  it('the table toggle shows the same numbers', async () => {
    const data = points(4);
    const el = await render(data);
    (el.querySelector('button.toggle') as HTMLButtonElement).click();
    refresh();
    expect(el.querySelector('svg[role="img"]')).toBeNull();
    const rows = Array.from(el.querySelectorAll('tbody tr')).map(r => Array.from(r.querySelectorAll('td')).map(td => Number(td.textContent)));
    expect(rows).toEqual(data.map(p => [p.sent, p.opened, p.replies]));
    expect(el.querySelector('thead')!.textContent).toContain('Opened (approx.)');
    (el.querySelector('button.toggle') as HTMLButtonElement).click();
    refresh();
    expect(el.querySelector('svg[role="img"]')).not.toBeNull();
  });

  it('hover and arrow keys show exact numbers for a day', async () => {
    const el = await render(points(5));
    fixture.componentInstance.onKey(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
    refresh();
    expect(fixture.componentInstance.hoverIndex).toBe(4);
    expect(el.querySelector('.tooltip')!.textContent).toContain('Sent');
    expect(el.querySelector('.tooltip')!.textContent).toContain('5');
    fixture.componentInstance.onKey(new KeyboardEvent('keydown', { key: 'Escape' }));
    refresh();
    expect(el.querySelector('.tooltip')).toBeNull();
  });

  it('an empty range shows a friendly empty state, not an empty axis', async () => {
    const el = await render(points(7, () => ({})));
    expect(el.querySelector('svg[role="img"]')).toBeNull();
    expect(el.querySelector('.empty')!.textContent).toContain('No emails were sent');
    expect(el.querySelector('button.toggle')).toBeNull();
  });

  it('formats days and describes the busiest day', () => {
    expect(shortDay('2026-03-05')).toBe('Mar 5');
    expect(describeTrend(points(3))).toContain('Busiest day');
    expect(describeTrend([])).toBe('No data.');
  });
});
