import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApprovalInfo } from './auth.models';

/** Registration approval links (F20). The token is the credential; no sign-in needed. */
@Injectable({ providedIn: 'root' })
export class ApprovalsService {
  constructor(private http: HttpClient) {}

  info(token: string): Observable<ApprovalInfo> {
    return this.http.get<ApprovalInfo>(`/api/v1/auth/approvals/${encodeURIComponent(token)}`);
  }

  decide(token: string, decision: 'approve' | 'reject'): Observable<void> {
    return this.http.post<void>(`/api/v1/auth/approvals/${encodeURIComponent(token)}/${decision}`, null);
  }
}
