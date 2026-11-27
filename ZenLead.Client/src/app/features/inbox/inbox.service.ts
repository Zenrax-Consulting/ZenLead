import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { PagedResult } from '../leads/leads.models';
import { InboxMessage, ReplyClassification, ReplyRequest, ThreadDetail, ThreadListItem, ThreadListQuery, UnreadCount } from './inbox.models';

const BASE = '/api/v1/inbox';

/** The F27 inbox API. Errors pass through untouched so components can read `error.code` / `error.message`. */
@Injectable({ providedIn: 'root' })
export class InboxService {
  constructor(private http: HttpClient) {}

  threads(query: ThreadListQuery): Observable<PagedResult<ThreadListItem>> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    if (query.classification) params = params.set('classification', query.classification);
    if (query.unread !== undefined) params = params.set('unread', query.unread);
    if (query.needsReview !== undefined) params = params.set('needsReview', query.needsReview);
    if (query.campaignId) params = params.set('campaignId', query.campaignId);
    if (query.q) params = params.set('q', query.q);
    return this.http.get<PagedResult<ThreadListItem>>(`${BASE}/threads`, { params });
  }

  /** Marks the thread read on the server. */
  thread(id: string): Observable<ThreadDetail> { return this.http.get<ThreadDetail>(`${BASE}/threads/${id}`); }

  setRead(id: string, isRead: boolean): Observable<void> { return this.http.post<void>(`${BASE}/threads/${id}/read`, { isRead }); }

  reply(id: string, request: ReplyRequest): Observable<InboxMessage> {
    return this.http.post<InboxMessage>(`${BASE}/threads/${id}/reply`, request);
  }

  reclassify(id: string, classification: ReplyClassification): Observable<ThreadListItem> {
    return this.http.post<ThreadListItem>(`${BASE}/threads/${id}/classification`, { classification });
  }

  unreadCount(): Observable<UnreadCount> { return this.http.get<UnreadCount>(`${BASE}/unread-count`); }
}
