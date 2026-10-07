import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CreateLeadRequest, Lead } from './leads.models';

@Injectable({ providedIn: 'root' })
export class LeadsService {
  constructor(private http: HttpClient) {}

  list(): Observable<Lead[]> {
    return this.http.get<Lead[]>('/api/v1/leads');
  }

  create(request: CreateLeadRequest): Observable<Lead> {
    return this.http.post<Lead>('/api/v1/leads', request);
  }
}
