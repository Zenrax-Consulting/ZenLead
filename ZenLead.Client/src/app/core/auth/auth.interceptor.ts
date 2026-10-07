import { Injectable } from '@angular/core';
import { HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, shareReplay, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { AuthResponse } from './auth.models';

@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  /** Shared by every request that hits a 401 at the same time, so the rotating refresh token is only used once. */
  private refreshInFlight$: Observable<AuthResponse> | null = null;

  constructor(private auth: AuthService, private router: Router) {}

  intercept(req: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    return next.handle(this.withAuthHeader(req)).pipe(
      catchError(error => {
        if (error.status === 401 && !req.url.includes('/auth/')) {
          return this.refreshOnce().pipe(
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

  private refreshOnce(): Observable<AuthResponse> {
    if (!this.refreshInFlight$) {
      this.refreshInFlight$ = this.auth.refresh().pipe(
        finalize(() => (this.refreshInFlight$ = null)),
        shareReplay({ bufferSize: 1, refCount: false })
      );
    }
    return this.refreshInFlight$;
  }

  private withAuthHeader(req: HttpRequest<unknown>): HttpRequest<unknown> {
    const token = this.auth.getAccessToken();
    return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  }
}
