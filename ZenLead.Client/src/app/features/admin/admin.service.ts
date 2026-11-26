import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CreateWorkspaceRequest, WorkspaceAdmin } from './admin.models';

/** Platform-admin API. Never requests tenant data. */
@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly url = '/api/v1/admin/workspaces';

  constructor(private http: HttpClient) {}

  list(): Observable<WorkspaceAdmin[]> {
    return this.http.get<WorkspaceAdmin[]>(this.url);
  }

  create(request: CreateWorkspaceRequest): Observable<WorkspaceAdmin> {
    return this.http.post<WorkspaceAdmin>(this.url, request);
  }
}
