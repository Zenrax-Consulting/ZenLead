import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';

describe('AuthService password flows (F21)', () => {
  let service: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('posts the email to forgot-password', () => {
    service.forgotPassword('a@acme.com').subscribe();
    const req = http.expectOne('/api/v1/auth/forgot-password');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'a@acme.com' });
    req.flush({ message: 'neutral' });
  });

  it('posts the token and new password to reset-password without touching the session', () => {
    service.resetPassword('tok', 'N3w!Passw0rd').subscribe();
    const req = http.expectOne('/api/v1/auth/reset-password');
    expect(req.request.body).toEqual({ token: 'tok', newPassword: 'N3w!Passw0rd' });
    req.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.isAuthenticated()).toBe(false);
  });

  it('changePassword stores the returned session', () => {
    service.changePassword('Old!Passw0rd', 'N3w!Passw0rd').subscribe();
    const req = http.expectOne('/api/v1/auth/change-password');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ currentPassword: 'Old!Passw0rd', newPassword: 'N3w!Passw0rd' });
    req.flush({ accessToken: 'new-access', refreshToken: 'new-refresh' });

    expect(service.getAccessToken()).toBe('new-access');
    expect(localStorage.getItem('zenlead_refresh_token')).toBe('new-refresh');
  });

  it('changePassword keeps the old session when it fails', () => {
    localStorage.setItem('zenlead_refresh_token', 'old-refresh');
    service.changePassword('wrong', 'N3w!Passw0rd').subscribe({ error: () => {} });
    http.expectOne('/api/v1/auth/change-password').flush({ code: 'wrong_password' }, { status: 400, statusText: 'Bad Request' });

    expect(localStorage.getItem('zenlead_refresh_token')).toBe('old-refresh');
  });
});
