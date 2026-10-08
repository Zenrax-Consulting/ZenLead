import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LeadsService } from './leads.service';

describe('LeadsService', () => {
  let service: LeadsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(LeadsService);
    http = TestBed.inject(HttpTestingController);
  });

  it('sends only page and pageSize when no filters are set', () => {
    service.list({ page: 1, pageSize: 25, q: '', status: undefined }).subscribe();

    const req = http.expectOne(r => r.url === '/api/v1/leads');
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    expect(req.request.params.get('page')).toBe('1');
    req.flush({ items: [], total: 0, page: 1, pageSize: 25 });
  });

  it('includes every filter that is set', () => {
    service.list({
      page: 3, pageSize: 50, q: 'acme', status: 'New', companyId: 'c1', source: 'Csv', sourceRunId: 'r1', sort: '-name'
    }).subscribe();

    const req = http.expectOne(r => r.url === '/api/v1/leads');
    expect(req.request.params.keys().sort()).toEqual(['companyId', 'page', 'pageSize', 'q', 'sort', 'source', 'sourceRunId', 'status']);
    expect(req.request.params.get('sort')).toBe('-name');
    req.flush({ items: [], total: 0, page: 3, pageSize: 50 });
  });

  it('returns the paged result as sent by the API', () => {
    let total = -1;
    service.list({ page: 1, pageSize: 25 }).subscribe(r => (total = r.total));

    http.expectOne(r => r.url === '/api/v1/leads').flush({ items: [{ id: '1' }], total: 42, page: 1, pageSize: 25 });

    expect(total).toBe(42);
  });

  it('updates with PUT and deletes with DELETE', () => {
    service.update('l1', { name: 'J', title: null, status: 'New', companyName: null, companyDomain: null }).subscribe();
    expect(http.expectOne('/api/v1/leads/l1').request.method).toBe('PUT');

    service.delete('l1').subscribe();
    const del = http.expectOne('/api/v1/leads/l1');
    expect(del.request.method).toBe('DELETE');
  });
});
