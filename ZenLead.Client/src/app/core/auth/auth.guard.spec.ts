import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { authGuard, superAdminGuard, tenantGuard } from './auth.guard';
import { AuthService } from './auth.service';

class FakeAuthService {
  authenticated = false;
  superAdmin = false;
  isAuthenticated(): boolean {
    return this.authenticated;
  }
  isSuperAdmin(): boolean {
    return this.superAdmin;
  }
}

class FakeRouter {
  navigateCalls: unknown[][] = [];
  navigate(path: unknown[]): Promise<boolean> {
    this.navigateCalls.push(path);
    return Promise.resolve(true);
  }
  parseUrl(url: string): UrlTree {
    return { redirectedTo: url } as unknown as UrlTree;
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

  describe('tenantGuard', () => {
    const run = () => TestBed.runInInjectionContext(() => tenantGuard({} as never, {} as never));

    it('sends anonymous users to /login', () => {
      expect(run()).toEqual({ redirectedTo: '/login' });
    });

    it('lets workspace users in', () => {
      fakeAuth.authenticated = true;
      expect(run()).toBe(true);
    });

    it('sends the super admin to the admin area', () => {
      fakeAuth.authenticated = true;
      fakeAuth.superAdmin = true;
      expect(run()).toEqual({ redirectedTo: '/admin/workspaces' });
    });
  });

  describe('superAdminGuard', () => {
    const run = () => TestBed.runInInjectionContext(() => superAdminGuard({} as never, {} as never));

    it('sends anonymous users to /login', () => {
      expect(run()).toEqual({ redirectedTo: '/login' });
    });

    it('sends workspace users to /leads', () => {
      fakeAuth.authenticated = true;
      expect(run()).toEqual({ redirectedTo: '/leads' });
    });

    it('lets the super admin in', () => {
      fakeAuth.authenticated = true;
      fakeAuth.superAdmin = true;
      expect(run()).toBe(true);
    });
  });
});
