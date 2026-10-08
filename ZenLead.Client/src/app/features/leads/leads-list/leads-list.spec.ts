import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { LeadsModule } from '../leads-module';
import { LeadSelectionService } from '../lead-selection.service';
import { Lead } from '../leads.models';
import { LeadsList, parseLeadQuery } from './leads-list';

const lead = (id: string, name = `Lead ${id}`): Lead => ({
  id, name, email: `${id}@acme.com`, title: null, status: 'New', createdAt: '2026-10-07T00:00:00Z',
  company: null, source: 'Manual', sourceRunId: null, emailVerificationStatus: 'Unverified'
});
const page = (items: Lead[], total = items.length, pageNo = 1) => ({ items, total, page: pageNo, pageSize: 25 });
const isList = (r: { url: string }) => r.url === '/api/v1/leads';

// The app is zoneless: async state changes only render if the component schedules a check.
describe('LeadsList', () => {
  let params$: BehaviorSubject<Record<string, string>>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  const setup = async (initial: Record<string, string> = {}) => {
    params$ = new BehaviorSubject(initial);
    navigate = vi.fn().mockResolvedValue(true);
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [LeadsModule],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParams: params$ } }
      ]
    }).compileComponents();
    TestBed.inject(Router).navigate = navigate as unknown as Router['navigate'];
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(LeadsList);
    await fixture.whenStable();
    return fixture;
  };

  it('turns the URL into the API query', async () => {
    await setup({ page: '2', pageSize: '50', q: 'acme', status: 'Replied', sort: '-name' });

    const req = http.expectOne(isList);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.get('q')).toBe('acme');
    expect(req.request.params.get('status')).toBe('Replied');
    expect(req.request.params.get('sort')).toBe('-name');
    req.flush(page([]));
  });

  it('drops malformed URL values instead of sending them', () => {
    expect(parseLeadQuery({ page: '-3', pageSize: '9999', status: 'Bogus', sort: 'password' }))
      .toEqual({ page: 1, pageSize: 25, q: undefined, status: undefined, source: undefined, companyId: undefined, sourceRunId: undefined, sort: undefined });
  });

  it('shows "Loading…" then the empty state when the account has no leads', async () => {
    const fixture = await setup();
    expect(fixture.nativeElement.textContent).toContain('Loading…');

    http.expectOne(isList).flush(page([]));
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).not.toContain('Loading…');
    expect(fixture.nativeElement.textContent).toContain('No leads yet');
  });

  it('shows a different empty state, with Clear filters, when filters match nothing', async () => {
    const fixture = await setup({ q: 'zzz' });

    http.expectOne(isList).flush(page([]));
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('No leads match');
    expect(fixture.nativeElement.textContent).toContain('Clear filters');
  });

  it('renders leads returned by the API', async () => {
    const fixture = await setup();
    http.expectOne(isList).flush(page([lead('1', 'Jane Doe')]));
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Jane Doe');
  });

  it('offers a retry when loading fails', async () => {
    const fixture = await setup();
    http.expectOne(isList).flush('x', { status: 500, statusText: 'err' });
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Failed to load leads.');
    fixture.componentInstance.load();
    http.expectOne(isList).flush(page([lead('1')]));
  });

  it('changing a filter navigates with page reset', async () => {
    const fixture = await setup({ page: '3' });
    http.expectOne(isList).flush(page([lead('1')], 100, 3));

    fixture.componentInstance.onStatus('Replied');

    const extras = navigate.mock.calls[0][1];
    expect(extras.queryParams).toMatchObject({ status: 'Replied', page: null });
    expect(extras.queryParamsHandling).toBe('merge');
  });

  it('debounces the search box by 300 ms and resets the page', async () => {
    const fixture = await setup({ page: '2' });
    http.expectOne(isList).flush(page([lead('1')], 100, 2));
    vi.useFakeTimers();
    try {

      fixture.componentInstance.searchControl.setValue('a');
      fixture.componentInstance.searchControl.setValue('ac');
      vi.advanceTimersByTime(299);
      expect(navigate).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);

      expect(navigate).toHaveBeenCalledTimes(1);
      expect(navigate.mock.calls[0][1].queryParams).toMatchObject({ q: 'ac', page: null });
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps the selection when the page changes, and the header checkbox selects exactly this page', async () => {
    const fixture = await setup();
    const selection = TestBed.inject(LeadSelectionService);
    http.expectOne(isList).flush(page([lead('a'), lead('b')], 4));

    fixture.componentInstance.toggleAllOnPage(true);
    expect(selection.selected.sort()).toEqual(['a', 'b']);
    expect(fixture.componentInstance.allOnPageSelected).toBe(true);

    params$.next({ page: '2' });
    http.expectOne(isList).flush(page([lead('c'), lead('d')], 4, 2));

    expect(selection.selected.sort()).toEqual(['a', 'b']);   // page 1 selection survives
    expect(fixture.componentInstance.allOnPageSelected).toBe(false);

    fixture.componentInstance.toggleAllOnPage(true);
    expect(selection.selected.sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('records the last query for the detail page and F24', async () => {
    await setup({ q: 'acme' });
    const selection = TestBed.inject(LeadSelectionService);

    http.expectOne(isList).flush(page([lead('1')], 7));

    expect(selection.lastQuery?.q).toBe('acme');
    expect(selection.lastTotal).toBe(7);
  });

  it('hides the discovery/campaign actions while their features are off', async () => {
    const fixture = await setup();
    http.expectOne(isList).flush(page([lead('1')]));
    TestBed.inject(LeadSelectionService).toggle('1');
    fixture.componentRef.changeDetectorRef.markForCheck();
    await fixture.whenStable();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('1 selected');
    expect(text).not.toContain('Create target profile');
    expect(text).not.toContain('Add to campaign');
  });
});
