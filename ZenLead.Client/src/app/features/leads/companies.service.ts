import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CompanySummary } from './leads.models';

@Injectable({ providedIn: 'root' })
export class CompaniesService {
  constructor(private http: HttpClient) {}

  search(q: string): Observable<CompanySummary[]> {
    let params = new HttpParams();
    if (q) params = params.set('q', q);
    return this.http.get<CompanySummary[]>('/api/v1/companies', { params });
  }

  getById(id: string): Observable<CompanySummary> {
    return this.http.get<CompanySummary>(`/api/v1/companies/${id}`);
  }
}
