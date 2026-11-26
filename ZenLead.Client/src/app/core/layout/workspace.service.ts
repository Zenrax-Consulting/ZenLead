import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  constructor(private http: HttpClient) {}

  current(): Observable<{ id: string; name: string }> {
    return this.http.get<{ id: string; name: string }>('/api/v1/workspaces/current');
  }

  /** IANA id, e.g. "Asia/Dubai" (F18); campaign send windows without their own zone are evaluated in it. */
  updateTimeZone(timeZone: string): Observable<void> {
    return this.http.put<void>('/api/v1/workspaces/current/time-zone', { timeZone });
  }
}
