import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CampaignsService } from './campaigns.service';
import { Campaign } from './campaigns.models';

describe('CampaignsService', () => {
  let service: CampaignsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CampaignsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('enroll sends only leadIds when given ids', () => {
    service.enroll('c1', { leadIds: ['a', 'b'] }, true).subscribe();
    const req = http.expectOne('/api/v1/campaigns/c1/enroll');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ leadIds: ['a', 'b'], dryRun: true });
    expect('filter' in req.request.body).toBe(false);
    req.flush({});
  });

  it('enroll sends only the filter when given a filter', () => {
    service.enroll('c1', { filter: { page: 1, pageSize: 25, q: 'acme' } }, false).subscribe();
    const req = http.expectOne('/api/v1/campaigns/c1/enroll');
    expect(req.request.body).toEqual({ filter: { page: 1, pageSize: 25, q: 'acme' }, dryRun: false });
    expect('leadIds' in req.request.body).toBe(false);
    req.flush({});
  });

  it('normalises sendDays strings to flags and sorts steps', () => {
    let campaign: Campaign | undefined;
    service.get('c1').subscribe(c => campaign = c);
    http.expectOne('/api/v1/campaigns/c1').flush({
      id: 'c1', sendDays: 'Mon, Wed',
      steps: [{ id: 's2', order: 2 }, { id: 's1', order: 1 }]
    });
    expect(campaign!.sendDays).toBe(5);
    expect(campaign!.steps.map(s => s.id)).toEqual(['s1', 's2']);
  });

  it('uses the documented paths and verbs', () => {
    const step = { delayDays: 0, subjectTemplate: 's', bodyTemplate: 'b', useAiPersonalisation: false };
    service.list().subscribe();
    service.update('c1', { name: 'n', fromName: null, dailySendCap: 30, windowStart: '09:00', windowEnd: '17:00', sendDays: 31, timeZoneId: null }).subscribe();
    service.addStep('c1', step).subscribe();
    service.updateStep('c1', 's1', step).subscribe();
    service.deleteStep('c1', 's1').subscribe();
    service.reorderSteps('c1', ['s2', 's1']).subscribe();
    service.activate('c1').subscribe();
    service.pause('c1').subscribe();
    service.activationPreview('c1').subscribe();
    service.preview('c1', 'l1', 's1').subscribe();
    service.senderStatus().subscribe();
    service.enrollments('c1', 'Active', 2, 50).subscribe();

    const expectReq = (method: string, url: string, body?: unknown) => {
      const req = http.expectOne(r => r.method === method && r.url === url);
      if (body !== undefined) expect(req.request.body).toEqual(body);
      return req;
    };
    expectReq('GET', '/api/v1/campaigns').flush([]);
    expectReq('PUT', '/api/v1/campaigns/c1').flush({ steps: [] });
    expectReq('POST', '/api/v1/campaigns/c1/steps', step).flush({});
    expectReq('PUT', '/api/v1/campaigns/c1/steps/s1', step).flush({});
    expectReq('DELETE', '/api/v1/campaigns/c1/steps/s1').flush(null);
    expectReq('POST', '/api/v1/campaigns/c1/steps/reorder', { orderedStepIds: ['s2', 's1'] }).flush([]);
    expectReq('POST', '/api/v1/campaigns/c1/activate').flush({ steps: [] });
    expectReq('POST', '/api/v1/campaigns/c1/pause').flush({ steps: [] });
    expectReq('GET', '/api/v1/campaigns/c1/activation-preview').flush({});
    expectReq('POST', '/api/v1/campaigns/c1/preview', { leadId: 'l1', stepId: 's1' }).flush({});
    expectReq('GET', '/api/v1/campaigns/sender').flush({});
    const list = expectReq('GET', '/api/v1/campaigns/c1/enrollments');
    expect(list.request.params.get('status')).toBe('Active');
    expect(list.request.params.get('page')).toBe('2');
    expect(list.request.params.get('pageSize')).toBe('50');
    list.flush({ items: [], total: 0, page: 2, pageSize: 50 });
  });
});
