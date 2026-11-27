import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AnalyticsModule } from '../analytics-module';
import { AnalyticsTotals } from '../analytics.models';
import { OPENS_CAVEAT, StatTileModel, buildTiles } from '../tiles';
import { StatTile } from './stat-tile';

const totals = (over: Partial<AnalyticsTotals> = {}): AnalyticsTotals => ({
  sent: 100, delivered: 97, opened: 40, bounced: 3, contactedLeads: 50, repliedLeads: 5,
  deliveredRate: 0.97, openRate: 0.4124, replyRate: 0.1, bounceRate: 0.03, opensApproximate: true, ...over
});

describe('StatTile', () => {
  let fixture: ComponentFixture<StatTile>;

  const render = async (tile: StatTileModel) => {
    await TestBed.configureTestingModule({ imports: [AnalyticsModule] }).compileComponents();
    fixture = TestBed.createComponent(StatTile);
    fixture.componentRef.setInput('tile', tile);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };

  it('carries the approximate-opens caveat on Opened and nowhere else', () => {
    const tiles = buildTiles(totals());
    for (const t of tiles) {
      if (t.key === 'opened') expect(t.info).toContain(OPENS_CAVEAT);
      else expect(t.info ?? '').not.toContain('approximate');
    }
    expect(tiles.find(t => t.key === 'opened')!.label).toBe('Opened ≈');
  });

  it('renders the caveat as an accessible info button on the Opened tile', async () => {
    const el = await render(buildTiles(totals()).find(t => t.key === 'opened')!);
    expect(el.textContent).toContain('Opened ≈');
    expect(el.textContent).toContain('41.2% of delivered (approx.)');
    expect(el.querySelector('button.info')!.getAttribute('aria-label')).toContain('Apple Mail Privacy Protection');
  });

  it('shows no info button on Sent', async () => {
    const el = await render(buildTiles(totals()).find(t => t.key === 'sent')!);
    expect(el.querySelector('button.info')).toBeNull();
    expect(el.querySelector('.value')!.textContent).toContain('100');
  });

  it('null rates show a dash with the reason, never NaN', () => {
    const tiles = buildTiles(totals({ sent: 0, delivered: 0, opened: 0, bounced: 0, contactedLeads: 0, repliedLeads: 0,
      deliveredRate: null, openRate: null, replyRate: null, bounceRate: null }));
    expect(tiles.map(t => t.rateLine).join(' ')).not.toContain('NaN');
    const delivered = tiles.find(t => t.key === 'delivered')!;
    expect(delivered.rateLine).toBe('— of sent');
    expect(delivered.info).toContain('No emails were sent');
    expect(tiles.find(t => t.key === 'opened')!.info).toContain('No emails were delivered');
  });

  it('warns in words when the bounce rate is 2 % or more', async () => {
    expect(buildTiles(totals({ bounceRate: 0.019 })).find(t => t.key === 'bounced')!.warning).toBeNull();
    const el = await render(buildTiles(totals({ bounceRate: 0.02 })).find(t => t.key === 'bounced')!);
    expect(el.querySelector('.warning')!.textContent).toContain('High bounce rate');
  });

  it('replied rate is of leads contacted', () => {
    expect(buildTiles(totals()).find(t => t.key === 'replied')!.rateLine).toBe('10.0% of leads contacted');
  });
});
