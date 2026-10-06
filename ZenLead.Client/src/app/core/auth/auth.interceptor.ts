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
        if (error.status === 401 && !req.url.includes('/auth/')) {
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
