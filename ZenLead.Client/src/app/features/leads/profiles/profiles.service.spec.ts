import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { emptyCriteria } from './profiles.models';
import { ProfilesService } from './profiles.service';

describe('ProfilesService', () => {
  let service: ProfilesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ProfilesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists, creates, updates and deletes profiles on /target-profiles', () => {
    const request = { name: 'US CTOs', criteria: { ...emptyCriteria(), countries: ['US'] } };

    service.list().subscribe();
    expect(http.expectOne('/api/v1/target-profiles').request.method).toBe('GET');

    service.create(request).subscribe();
    const post = http.expectOne('/api/v1/target-profiles');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(request);

    service.update('p1', request).subscribe();
    expect(http.expectOne('/api/v1/target-profiles/p1').request.method).toBe('PUT');

    service.delete('p1').subscribe();
    expect(http.expectOne('/api/v1/target-profiles/p1').request.method).toBe('DELETE');
  });

  it('posts lead ids and the only-replied flag to from-leads', () => {
    service.suggestFromLeads(['a', 'b'], true).subscribe();

    const req = http.expectOne('/api/v1/target-profiles/from-leads');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ leadIds: ['a', 'b'], onlyReplied: true });
  });

  it('starts a run with maxLeads and reads run progress', () => {
    service.startRun('p1', 20).subscribe();
    const start = http.expectOne('/api/v1/target-profiles/p1/runs');
    expect(start.request.method).toBe('POST');
    expect(start.request.body).toEqual({ maxLeads: 20 });

    service.getRun('r1').subscribe();
    expect(http.expectOne('/api/v1/discovery/runs/r1').request.method).toBe('GET');
  });

  it('filters runs by profile only when one is given, and reads credits', () => {
    service.listRuns().subscribe();
    expect(http.expectOne(r => r.url === '/api/v1/discovery/runs').request.params.keys()).toEqual([]);

    service.listRuns('p1').subscribe();
    expect(http.expectOne(r => r.url === '/api/v1/discovery/runs').request.params.get('targetProfileId')).toBe('p1');

    service.credits().subscribe();
    expect(http.expectOne('/api/v1/discovery/credits').request.method).toBe('GET');
  });
});
