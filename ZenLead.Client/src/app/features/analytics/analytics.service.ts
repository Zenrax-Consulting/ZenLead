import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CampaignAnalytics, DateRangeParams, WorkspaceAnalytics } from './analytics.models';

const BASE = '/api/v1/analytics';

/** The F28 analytics API. */
@Injectable({ providedIn: 'root' })
export class AnalyticsService {
  constructor(private http: HttpClient) {}

  workspace(range: DateRangeParams): Observable<WorkspaceAnalytics> {
    return this.http.get<WorkspaceAnalytics>(`${BASE}/workspace/summary`, { params: params(range) });
  }

  campaign(id: string, range: DateRangeParams): Observable<CampaignAnalytics> {
    return this.http.get<CampaignAnalytics>(`${BASE}/campaigns/${encodeURIComponent(id)}/summary`, { params: params(range) });
  }
}

function params(range: DateRangeParams): HttpParams {
  return new HttpParams().set('from', range.from).set('to', range.to);
}
