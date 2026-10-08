import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ComposedEmail, CreateLeadRequest, Lead, LeadQuery, PagedResult, UpdateLeadRequest } from './leads.models';

@Injectable({ providedIn: 'root' })
export class LeadsService {
  constructor(private http: HttpClient) {}

  list(query: LeadQuery): Observable<PagedResult<Lead>> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    for (const key of ['q', 'status', 'companyId', 'source', 'sourceRunId', 'sort'] as const) {
      const value = query[key];
      if (value) params = params.set(key, value);
    }
    return this.http.get<PagedResult<Lead>>('/api/v1/leads', { params });
  }

  create(request: CreateLeadRequest): Observable<Lead> {
    return this.http.post<Lead>('/api/v1/leads', request);
  }

  getById(id: string): Observable<Lead> {
    return this.http.get<Lead>(`/api/v1/leads/${id}`);
  }

  update(id: string, request: UpdateLeadRequest): Observable<Lead> {
    return this.http.put<Lead>(`/api/v1/leads/${id}`, request);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/leads/${id}`);
  }

  composeEmail(leadId: string, context?: string): Observable<ComposedEmail> {
    return this.http.post<ComposedEmail>('/api/v1/ai/compose-email', { leadId, context });
  }
}
