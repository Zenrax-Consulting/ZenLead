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

  logout(): void {
    this.accessToken = null;
    localStorage.removeItem(REFRESH_TOKEN_KEY);
  }

  private storeSession(response: AuthResponse): void {
    this.accessToken = response.accessToken;
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
  }
}
