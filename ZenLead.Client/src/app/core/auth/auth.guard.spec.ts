import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

class FakeAuthService {
  authenticated = false;
  isAuthenticated(): boolean {
    return this.authenticated;
  }
}

class FakeRouter {
  navigateCalls: unknown[][] = [];
  navigate(path: unknown[]): Promise<boolean> {
    this.navigateCalls.push(path);
    return Promise.resolve(true);
  }
}

describe('authGuard', () => {
  let fakeAuth: FakeAuthService;
  let fakeRouter: FakeRouter;

  beforeEach(() => {
    fakeAuth = new FakeAuthService();
    fakeRouter = new FakeRouter();

    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: fakeAuth },
        { provide: Router, useValue: fakeRouter }
      ]
    });
  });

  it('allows navigation when authenticated', () => {
    fakeAuth.authenticated = true;
    const result = TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    expect(result).toBe(true);
    expect(fakeRouter.navigateCalls.length).toBe(0);
  });

  it('redirects to /login and blocks navigation when not authenticated', () => {
    const result = TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    expect(result).toBe(false);
    expect(fakeRouter.navigateCalls).toContainEqual(['/login']);
  });
});
