import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Credits, DiscoveryRun, SuggestedProfile, TargetProfile, TargetProfileRequest } from './profiles.models';

@Injectable({ providedIn: 'root' })
export class ProfilesService {
  constructor(private http: HttpClient) {}

  list(): Observable<TargetProfile[]> {
    return this.http.get<TargetProfile[]>('/api/v1/target-profiles');
  }

  create(request: TargetProfileRequest): Observable<TargetProfile> {
    return this.http.post<TargetProfile>('/api/v1/target-profiles', request);
  }

  update(id: string, request: TargetProfileRequest): Observable<TargetProfile> {
    return this.http.put<TargetProfile>(`/api/v1/target-profiles/${id}`, request);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/v1/target-profiles/${id}`);
  }

  suggestFromLeads(leadIds: string[], onlyReplied = false): Observable<SuggestedProfile> {
    return this.http.post<SuggestedProfile>('/api/v1/target-profiles/from-leads', { leadIds, onlyReplied });
  }

  startRun(profileId: string, maxLeads: number): Observable<DiscoveryRun> {
    return this.http.post<DiscoveryRun>(`/api/v1/target-profiles/${profileId}/runs`, { maxLeads });
  }

  getRun(id: string): Observable<DiscoveryRun> {
    return this.http.get<DiscoveryRun>(`/api/v1/discovery/runs/${id}`);
  }

  listRuns(targetProfileId?: string): Observable<DiscoveryRun[]> {
    let params = new HttpParams();
    if (targetProfileId) params = params.set('targetProfileId', targetProfileId);
    return this.http.get<DiscoveryRun[]>('/api/v1/discovery/runs', { params });
  }

  credits(): Observable<Credits> {
    return this.http.get<Credits>('/api/v1/discovery/credits');
  }
}
