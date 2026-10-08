import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class OpsService {
  constructor(private http: HttpClient) {}

  status(): Observable<{ canOpenDashboard: boolean }> {
    return this.http.get<{ canOpenDashboard: boolean }>('/api/v1/ops/status');
  }

  /** Asks the API for the short-lived dashboard cookie, then opens the dashboard in a new tab. */
  openDashboard(): Observable<void> {
    return this.http.post<void>('/api/v1/ops/hangfire-session', {}).pipe(tap(() => window.open('/hangfire', '_blank')));
  }
}
