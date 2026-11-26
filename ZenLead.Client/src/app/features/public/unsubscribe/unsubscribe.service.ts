import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface UnsubscribeInfo {
  email: string;               // masked, e.g. j***@acme.com
  alreadyUnsubscribed: boolean;
}

/** Public unsubscribe links (F23). The signed token is the credential; no sign-in needed. */
@Injectable({ providedIn: 'root' })
export class UnsubscribeService {
  constructor(private http: HttpClient) {}

  info(token: string): Observable<UnsubscribeInfo> {
    return this.http.get<UnsubscribeInfo>(`/api/v1/unsubscribe/${encodeURIComponent(token)}`);
  }

  confirm(token: string): Observable<void> {
    return this.http.post<void>(`/api/v1/unsubscribe/${encodeURIComponent(token)}`, null);
  }
}
