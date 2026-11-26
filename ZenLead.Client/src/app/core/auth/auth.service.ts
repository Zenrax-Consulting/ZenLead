import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { AuthResponse, LoginRequest, RegisterRequest } from './auth.models';
import { Claims, decodeClaims } from './jwt-claims';

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

  /** Decoded access-token claims — UI hints only; the API enforces access. */
  claims(): Claims | null {
    return decodeClaims(this.accessToken);
  }

  isSuperAdmin(): boolean {
    return this.claims()?.role === 'SuperAdmin';
  }

  /** Where a signed-in user belongs: the platform admin area for the super admin, the leads list for everyone else. */
  homeRoute(): string {
    return this.isSuperAdmin() ? '/admin/workspaces' : '/leads';
  }

  /** Asks to join a workspace. Answers 202 with no tokens: the account stays unusable until the workspace admin approves it. */
  register(request: RegisterRequest): Observable<void> {
    return this.http.post<void>('/api/v1/auth/register', request);
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

  /** Called once at app startup (see provideAppInitializer in app-module.ts) to silently re-auth on page reload. */
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

  /** Always answers 200 with the same neutral message, whether or not the address has an account (F21). */
  forgotPassword(email: string): Observable<void> {
    return this.http.post<void>('/api/v1/auth/forgot-password', { email });
  }

  resetPassword(token: string, newPassword: string): Observable<void> {
    return this.http.post<void>('/api/v1/auth/reset-password', { token, newPassword });
  }

  /** Every other session is signed out server-side; this one continues with the returned pair. */
  changePassword(currentPassword: string, newPassword: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>('/api/v1/auth/change-password', { currentPassword, newPassword }).pipe(
      tap(response => this.storeSession(response))
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
