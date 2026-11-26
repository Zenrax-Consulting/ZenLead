import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { WorkspaceOption } from './auth/auth.models';

/** Anonymous type-ahead for the registration form: ids and names only. */
@Injectable({ providedIn: 'root' })
export class WorkspacePickerService {
  constructor(private http: HttpClient) {}

  search(q: string): Observable<WorkspaceOption[]> {
    return this.http.get<WorkspaceOption[]>('/api/v1/workspaces', { params: new HttpParams().set('q', q) });
  }
}
