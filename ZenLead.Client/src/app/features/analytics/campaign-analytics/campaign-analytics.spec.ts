import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { AnalyticsModule } from '../analytics-module';
import { CampaignAnalytics as CampaignAnalyticsData, StepBreakdown } from '../analytics.models';
import { AnalyticsService } from '../analytics.service';
import { CampaignAnalytics } from './campaign-analytics';

const step = (order: number, subject: string, sent = 5): StepBreakdown =>
  ({ stepId: `s${order}`, order, subjectTemplate: subject, sent, delivered: sent, opened: 1, bounced: 0, replies: 1 });

const data = (over: Partial<CampaignAnalyticsData> = {}): CampaignAnalyticsData => ({
  campaignId: 'c1', name: 'Spring outreach', status: 'Active', from: '2026-03-01', to: '2026-03-10', timeZone: 'UTC',
  totals: { sent: 10, delivered: 10, opened: 2, bounced: 0, contactedLeads: 5, repliedLeads: 2, deliveredRate: 1, openRate: 0.2, replyRate: 0.4, bounceRate: 0, opensApproximate: true },
  steps: [step(2, 'Following up'), step(1, 'Quick question'), step(3, 'Last note', 0)],
  trend: [{ day: '2026-03-01', sent: 10, opened: 2, replies: 2 }], ...over
});

describe('CampaignAnalytics', () => {
  let fixture: ComponentFixture<CampaignAnalytics>;
  let campaign: ReturnType<typeof vi.fn>;

  const setup = async (response: () => ReturnType<AnalyticsService['campaign']>) => {
    campaign = vi.fn(response);
    await TestBed.configureTestingModule({
      imports: [AnalyticsModule],
      providers: [
        provideRouter([]),
        { provide: AnalyticsService, useValue: { campaign } },
        { provide: ActivatedRoute, useValue: { paramMap: new BehaviorSubject(convertToParamMap({ id: 'c1' })), queryParamMap: new BehaviorSubject(convertToParamMap({ range: '7d' })) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CampaignAnalytics);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };

  it('requests the campaign with the range and lists steps ordered by order', async () => {
    const el = await setup(() => of(data()));
    expect(campaign).toHaveBeenCalledWith('c1', expect.objectContaining({ from: expect.any(String), to: expect.any(String) }));
    expect(fixture.componentInstance.data!.steps.map(s => s.order)).toEqual([1, 2, 3]);
    const titles = Array.from(el.querySelectorAll('app-bar-list .step-row h3')).map(h => h.textContent!.trim());
    expect(titles).toEqual(['Step 1 · Quick question', 'Step 2 · Following up', 'Step 3 · Last note']);
    expect(el.querySelector('.banner')).toBeNull();
    expect(el.querySelector('h1')!.textContent).toContain('Spring outreach');
  });

  it('shows the Draft banner', async () => {
    const el = await setup(() => of(data({ status: 'Draft', steps: [step(1, 'Hello', 0)] })));
    expect(el.querySelector('.banner')!.textContent).toContain('Nothing sent yet');
  });

  it('another workspace’s campaign is "not found"', async () => {
    const el = await setup(() => throwError(() => ({ status: 404 })));
    expect(el.textContent).toContain('Campaign not found');
  });

  it('shows an error with retry', async () => {
    let fail = true;
    const el = await setup(() => (fail ? throwError(() => ({ status: 500 })) : of(data())));
    expect(el.querySelector('.error')!.textContent).toContain('Could not load');
    fail = false;
    (el.querySelector('.error button') as HTMLButtonElement).click();
    expect(campaign).toHaveBeenCalledTimes(2);
  });
});
