import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HTTP_INTERCEPTORS, HttpClient, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { AuthInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

class FakeAuthService {
  accessToken: string | null = 'initial-token';
  refreshCalls = 0;
  refreshShouldFail = false;

  getAccessToken(): string | null {
    return this.accessToken;
  }

  /** When set, refresh() waits on this subject so tests can hold the refresh open while several requests 401. */
  pendingRefresh: Subject<{ accessToken: string; refreshToken: string }> | null = null;

  refresh() {
    this.refreshCalls++;
    if (this.pendingRefresh) return this.pendingRefresh;
    if (this.refreshShouldFail) {
      return throwError(() => new Error('refresh failed'));
    }
    this.accessToken = 'refreshed-token';
    return of({ accessToken: 'refreshed-token', refreshToken: 'new-refresh' });
  }

  logout(): void {
    this.accessToken = null;
  }
}

class FakeRouter {
  navigateCalls: unknown[][] = [];
  navigate(path: unknown[]): Promise<boolean> {
    this.navigateCalls.push(path);
    return Promise.resolve(true);
  }
}

describe('AuthInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let fakeAuth: FakeAuthService;
  let fakeRouter: FakeRouter;

  beforeEach(() => {
    fakeAuth = new FakeAuthService();
    fakeRouter = new FakeRouter();

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptorsFromDi()),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: fakeAuth },
        { provide: Router, useValue: fakeRouter },
        { provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true }
      ]
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('attaches the Authorization header from the in-memory access token', () => {
    http.get('/api/v1/leads').subscribe();

    const req = httpMock.expectOne('/api/v1/leads');
    expect(req.request.headers.get('Authorization')).toBe('Bearer initial-token');
    req.flush([]);
  });

  it('does not attach an Authorization header when there is no access token', () => {
    fakeAuth.accessToken = null;

    http.get('/api/v1/leads').subscribe();

    const req = httpMock.expectOne('/api/v1/leads');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush([]);
  });

  it('on a 401, refreshes once and retries the original request with the new token', () => {
    http.get('/api/v1/leads').subscribe();

    const firstReq = httpMock.expectOne('/api/v1/leads');
    firstReq.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    const retryReq = httpMock.expectOne('/api/v1/leads');
    expect(retryReq.request.headers.get('Authorization')).toBe('Bearer refreshed-token');
    expect(fakeAuth.refreshCalls).toBe(1);
    retryReq.flush([]);
  });

  it('does not attempt a refresh when the 401 comes from /auth/refresh itself', () => {
    http.post('/api/v1/auth/refresh', {}).subscribe({ error: () => {} });

    const req = httpMock.expectOne('/api/v1/auth/refresh');
    req.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(fakeAuth.refreshCalls).toBe(0); // guards against an infinite refresh loop
  });

  it('does not attempt a refresh when a login attempt returns 401', () => {
    http.post('/api/v1/auth/login', {}).subscribe({ error: () => {} });

    const req = httpMock.expectOne('/api/v1/auth/login');
    req.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(fakeAuth.refreshCalls).toBe(0);
    expect(fakeRouter.navigateCalls.length).toBe(0);
  });

  it('logs out and redirects to /login when the refresh itself fails', () => {
    fakeAuth.refreshShouldFail = true;

    http.get('/api/v1/leads').subscribe({ error: () => {} });

    const firstReq = httpMock.expectOne('/api/v1/leads');
    firstReq.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(fakeAuth.accessToken).toBeNull();
    expect(fakeRouter.navigateCalls).toContainEqual(['/login']);
  });

  it('shares one refresh across concurrent 401s and retries every request', () => {
    const pending = new Subject<{ accessToken: string; refreshToken: string }>();
    fakeAuth.pendingRefresh = pending;
    const results: unknown[] = [];
    const urls = ['/api/v1/a', '/api/v1/b', '/api/v1/c'];

    urls.forEach(url => http.get(url).subscribe(r => results.push(r)));
    urls.forEach(url => httpMock.expectOne(url).flush('Unauthorized', { status: 401, statusText: 'Unauthorized' }));
    expect(fakeAuth.refreshCalls).toBe(1);

    fakeAuth.accessToken = 'refreshed-token';
    pending.next({ accessToken: 'refreshed-token', refreshToken: 'new-refresh' });
    pending.complete();

    urls.forEach(url => {
      const retry = httpMock.expectOne(url);
      expect(retry.request.headers.get('Authorization')).toBe('Bearer refreshed-token');
      retry.flush('ok');
    });
    expect(results.length).toBe(3);
    expect(fakeRouter.navigateCalls.length).toBe(0);
  });

  it('logs out and redirects once when a shared refresh fails', () => {
    const pending = new Subject<{ accessToken: string; refreshToken: string }>();
    fakeAuth.pendingRefresh = pending;
    http.get('/api/v1/a').subscribe({ error: () => {} });
    http.get('/api/v1/b').subscribe({ error: () => {} });

    httpMock.expectOne('/api/v1/a').flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne('/api/v1/b').flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    pending.error(new Error('refresh failed'));

    expect(fakeAuth.refreshCalls).toBe(1);
    expect(fakeAuth.accessToken).toBeNull();
    expect(fakeRouter.navigateCalls.length).toBe(2); // one per failed request, but a single refresh attempt
  });
});
