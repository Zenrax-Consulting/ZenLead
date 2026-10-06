# Feature 3 — Angular Auth Experience

**Branch:** `feature/angular-auth`
**Milestone:** 1 — Scaffolding
**Depends on:** Feature 2 (real `/api/v1/auth/*` endpoints to call).

## Goal
Register/login screens that call the real backend, hold the access token in memory only, persist the refresh token in `localStorage`, and silently restore a session on page reload.

## Current state (verified)
- Angular 21.2.0, **NgModule-based** (`"standalone": false"` is the schematic default in `angular.json`).
- Root module `src/app/app-module.ts`, root routing `src/app/app-routing-module.ts` (routes currently empty), root component `src/app/app.ts`/`app.html` (class `App`, still the default weather-forecast demo calling `/weatherforecast`).
- No `@angular/material`/`@angular/cdk`/`@angular/animations` installed yet.
- `src/proxy.conf.js` only proxies `/weatherforecast` — must add `/api` or `ng serve` requests 404 instead of reaching the API.
- `/weatherforecast` backend endpoint is deleted in Feature 2, so the current `app.ts` weather-fetch call would break regardless — cleaning it up here is required, not optional.

## Steps & files

### 1. `ng add @angular/material`
Run from `ZenLead.Client/`, select the **Azure/Blue** prebuilt theme, enable typography + browser animations (per the locked decision in [phase-1-poc-implementation-plan.md](../phase-1-poc-implementation-plan.md) §1). This updates `package.json`, adds a theme `<link>`/import to `src/styles.css`, and wires animations providers — let the schematic do it rather than hand-editing.

### `ZenLead.Client/src/app/core/auth/auth.models.ts` (new)
```typescript
export interface RegisterRequest {
  workspaceName: string;
  email: string;
  password: string;
  displayName: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
}
```

### `ZenLead.Client/src/app/core/auth/auth.service.ts` (new)
```typescript
import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { AuthResponse, LoginRequest, RegisterRequest } from './auth.models';

const REFRESH_TOKEN_KEY = 'zenlead_refresh_token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private accessToken: string | null = null;

  constructor(private http: HttpClient) {}

  getAccessToken(): string | null {
    return this.accessToken;
  }

  isAuthenticated(): boolean {
    return this.accessToken !== null;
  }

  register(request: RegisterRequest): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/v1/auth/register', request).pipe(
      tap(response => this.storeSession(response))
    );
  }

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/v1/auth/login', request).pipe(
      tap(response => this.storeSession(response))
    );
  }

  refresh(): Observable<AuthResponse> {
    const refreshToken = localStorage.getItem(REFRESH_TOKEN_KEY);
    return this.http.post<AuthResponse>('/api/v1/auth/refresh', { refreshToken }).pipe(
      tap(response => this.storeSession(response))
    );
  }

  /** Called once at app startup (see APP_INITIALIZER in app-module.ts) to silently re-auth on page reload. */
  tryRestoreSession(): Observable<boolean> {
    if (!localStorage.getItem(REFRESH_TOKEN_KEY)) return of(false);

    return this.refresh().pipe(
      map(() => true),
      catchError(() => {
        this.logout();
        return of(false);
      })
    );
  }

  logout(): void {
    this.accessToken = null;
    localStorage.removeItem(REFRESH_TOKEN_KEY);
  }

  private storeSession(response: AuthResponse): void {
    this.accessToken = response.accessToken;
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
  }
}
```

### `ZenLead.Client/src/app/core/auth/auth.interceptor.ts` (new)
```typescript
import { Injectable } from '@angular/core';
import { HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  constructor(private auth: AuthService, private router: Router) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(this.withAuthHeader(req)).pipe(
      catchError(error => {
        if (error.status === 401 && !req.url.includes('/auth/refresh')) {
          return this.auth.refresh().pipe(
            switchMap(() => next.handle(this.withAuthHeader(req))),
            catchError(refreshError => {
              this.auth.logout();
              this.router.navigate(['/login']);
              return throwError(() => refreshError);
            })
          );
        }
        return throwError(() => error);
      })
    );
  }

  private withAuthHeader(req: HttpRequest<unknown>): HttpRequest<unknown> {
    const token = this.auth.getAccessToken();
    return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  }
}
```

### `ZenLead.Client/src/app/core/auth/auth.interceptor.spec.ts` (new)
Per [CLAUDE.md](../../CLAUDE.md)'s testing conventions ("Angular gets targeted unit tests (auth interceptor, scheduling display logic)"), the interceptor is the one piece of frontend auth logic worth covering directly — it's where a refresh-loop bug or a missing header would silently break every authenticated request. Uses hand-written fake `AuthService`/`Router` stand-ins rather than a mocking library, consistent with the project's existing fake-based testing convention on the backend.
```typescript
import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { HTTP_INTERCEPTORS, HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

class FakeAuthService {
  accessToken: string | null = 'initial-token';
  refreshCalls = 0;
  refreshShouldFail = false;

  getAccessToken(): string | null {
    return this.accessToken;
  }

  refresh() {
    this.refreshCalls++;
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
      imports: [HttpClientTestingModule],
      providers: [
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

  it('logs out and redirects to /login when the refresh itself fails', () => {
    fakeAuth.refreshShouldFail = true;

    http.get('/api/v1/leads').subscribe({ error: () => {} });

    const firstReq = httpMock.expectOne('/api/v1/leads');
    firstReq.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(fakeAuth.accessToken).toBeNull();
    expect(fakeRouter.navigateCalls).toContainEqual(['/login']);
  });
});
```

### `ZenLead.Client/src/app/core/auth/auth.guard.ts` (new)
```typescript
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  if (auth.isAuthenticated()) return true;

  inject(Router).navigate(['/login']);
  return false;
};
```

### `ZenLead.Client/src/app/core/auth/auth.guard.spec.ts` (new)
```typescript
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
    const result = TestBed.runInInjectionContext(() => {
      fakeAuth.authenticated = true;
      return authGuard({} as never, {} as never);
    });

    expect(result).toBe(true);
    expect(fakeRouter.navigateCalls.length).toBe(0);
  });

  it('redirects to /login and blocks navigation when not authenticated', () => {
    const result = TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));

    expect(result).toBe(false);
    expect(fakeRouter.navigateCalls).toContainEqual(['/login']);
  });
});
```
The guard's `route`/`state` parameters are unused by `authGuard` itself, so the tests pass empty placeholders (`{} as never`) rather than building real `ActivatedRouteSnapshot`/`RouterStateSnapshot` instances.

### `ZenLead.Client/src/app/features/auth/register/register.ts` (new)
```typescript
import { Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-register',
  standalone: false,
  templateUrl: './register.html',
  styleUrl: './register.css'
})
export class Register {
  form: FormGroup = this.fb.group({
    workspaceName: ['', Validators.required],
    displayName: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]]
  });

  submitting = false;
  errorMessage: string | null = null;

  constructor(private fb: FormBuilder, private auth: AuthService, private router: Router) {}

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.register(this.form.getRawValue()).subscribe({
      next: () => this.router.navigate(['/login']),
      error: () => {
        this.submitting = false;
        this.errorMessage = 'Registration failed. Email may already be in use.';
      }
    });
  }
}
```

### `ZenLead.Client/src/app/features/auth/register/register.html` (new)
```html
<mat-card class="auth-card">
  <mat-card-title>Create your workspace</mat-card-title>
  <form [formGroup]="form" (ngSubmit)="submit()">
    <mat-form-field appearance="outline">
      <mat-label>Workspace name</mat-label>
      <input matInput formControlName="workspaceName">
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-label>Your name</mat-label>
      <input matInput formControlName="displayName">
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-label>Email</mat-label>
      <input matInput type="email" formControlName="email">
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-label>Password</mat-label>
      <input matInput type="password" formControlName="password">
    </mat-form-field>
    <p *ngIf="errorMessage" class="auth-error">{{ errorMessage }}</p>
    <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || submitting">
      Register
    </button>
  </form>
</mat-card>
```
(`register.css` — trivial `.auth-card { max-width: 400px; margin: 48px auto; } .auth-error { color: #b00020; }`, shared visually with `login.css`.)

### `ZenLead.Client/src/app/features/auth/login/login.ts` (new)
```typescript
import { Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: false,
  templateUrl: './login.html',
  styleUrl: './login.css'
})
export class Login {
  form: FormGroup = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required]
  });

  submitting = false;
  errorMessage: string | null = null;

  constructor(private fb: FormBuilder, private auth: AuthService, private router: Router) {}

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => this.router.navigate(['/leads']),
      error: () => {
        this.submitting = false;
        this.errorMessage = 'Invalid email or password.';
      }
    });
  }
}
```

### `ZenLead.Client/src/app/features/auth/login/login.html` (new)
```html
<mat-card class="auth-card">
  <mat-card-title>Log in</mat-card-title>
  <form [formGroup]="form" (ngSubmit)="submit()">
    <mat-form-field appearance="outline">
      <mat-label>Email</mat-label>
      <input matInput type="email" formControlName="email">
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-label>Password</mat-label>
      <input matInput type="password" formControlName="password">
    </mat-form-field>
    <p *ngIf="errorMessage" class="auth-error">{{ errorMessage }}</p>
    <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || submitting">
      Log in
    </button>
  </form>
</mat-card>
```

### `ZenLead.Client/src/app/app.ts` (modified — strip the weather-forecast demo)
```typescript
import { Component } from '@angular/core';

@Component({
  selector: 'app-root',
  standalone: false,
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {}
```

### `ZenLead.Client/src/app/app.html` (modified)
```html
<router-outlet></router-outlet>
```

### `ZenLead.Client/src/app/app.spec.ts` (modified — drop the weather-fetch test, keep a smoke test)
```typescript
import { TestBed } from '@angular/core/testing';
import { RouterModule } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [App],
      imports: [RouterModule.forRoot([])]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });
});
```

### `ZenLead.Client/src/app/app-module.ts` (modified)
```typescript
import { firstValueFrom } from 'rxjs';
import { HTTP_INTERCEPTORS, HttpClientModule } from '@angular/common/http';
import { APP_INITIALIZER, NgModule, provideBrowserGlobalErrorListeners } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { BrowserModule } from '@angular/platform-browser';
import { BrowserAnimationsModule } from '@angular/platform-browser/animations';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { AuthInterceptor } from './core/auth/auth.interceptor';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';

function initializeAuth(authService: AuthService) {
  return () => firstValueFrom(authService.tryRestoreSession());
}

@NgModule({
  declarations: [
    App,
    Register,
    Login
  ],
  imports: [
    BrowserModule, HttpClientModule, BrowserAnimationsModule, ReactiveFormsModule,
    MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule,
    AppRoutingModule
  ],
  providers: [
    provideBrowserGlobalErrorListeners(),
    { provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true },
    { provide: APP_INITIALIZER, useFactory: initializeAuth, deps: [AuthService], multi: true }
  ],
  bootstrap: [App]
})
export class AppModule { }
```

### `ZenLead.Client/src/app/app-routing-module.ts` (modified)
```typescript
import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';

const routes: Routes = [
  { path: '', redirectTo: 'login', pathMatch: 'full' },
  { path: 'register', component: Register },
  { path: 'login', component: Login }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
```
The guarded `/leads` route is deliberately **not** added here — there's no Leads component to point it at yet (that's Feature 4). Adding `canActivate: [authGuard]` on a route with no component would leave a dangling reference; Feature 4 adds the route and the guard together.

### `ZenLead.Client/src/proxy.conf.js` (modified)
```javascript
const { env } = require('process');

const target = env.ASPNETCORE_HTTPS_PORT ? `https://localhost:${env.ASPNETCORE_HTTPS_PORT}` :
  env.ASPNETCORE_URLS ? env.ASPNETCORE_URLS.split(';')[0] : 'https://localhost:7242';

const PROXY_CONFIG = [
  {
    context: [
      "/api",
    ],
    target,
    secure: false
  }
]

module.exports = PROXY_CONFIG;
```
`/weatherforecast` is removed from the proxy list since its backend controller is deleted in Feature 2.

## Verification
- `ng build` succeeds with no TypeScript strict-mode errors.
- `ng test` — `auth.interceptor.spec.ts` and `auth.guard.spec.ts` pass.
- `npm start` (or `dotnet run --project ZenLead.Api`, which launches it via SpaProxy) → `/register` creates a workspace and redirects to `/login` → `/login` authenticates and redirects to `/leads` (404 until Feature 4 adds that route — expected for this feature in isolation) → reloading the tab while logged in triggers the silent `APP_INITIALIZER` refresh (visible via a network call to `/api/v1/auth/refresh` on load) without bouncing to `/login`.
