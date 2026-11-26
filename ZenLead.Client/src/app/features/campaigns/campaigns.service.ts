import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { PagedResult } from '../leads/leads.models';
import {
  ActivationPreview, Campaign, CampaignListItem, CampaignRequest, EnrollResult, EnrollTarget, EnrollmentRow, EnrollmentStatus,
  PreviewResponse, SenderStatus, Step, StepRequest, parseSendDays
} from './campaigns.models';

const BASE = '/api/v1/campaigns';

/**
 * The F22/F23 campaign API. Errors are passed through untouched so components can read `error.code` / `error.problems`.
 * `sendDays` arrives as an enum string ("Weekdays", "Mon, Wed") and is normalised to flags here.
 */
@Injectable({ providedIn: 'root' })
export class CampaignsService {
  constructor(private http: HttpClient) {}

  list(): Observable<CampaignListItem[]> { return this.http.get<CampaignListItem[]>(BASE); }

  get(id: string): Observable<Campaign> { return this.http.get<Campaign>(`${BASE}/${id}`).pipe(map(normalise)); }

  create(request: CampaignRequest): Observable<Campaign> { return this.http.post<Campaign>(BASE, request).pipe(map(normalise)); }

  update(id: string, request: CampaignRequest): Observable<Campaign> {
    return this.http.put<Campaign>(`${BASE}/${id}`, request).pipe(map(normalise));
  }

  addStep(id: string, request: StepRequest): Observable<Step> { return this.http.post<Step>(`${BASE}/${id}/steps`, request); }

  updateStep(id: string, stepId: string, request: StepRequest): Observable<Step> {
    return this.http.put<Step>(`${BASE}/${id}/steps/${stepId}`, request);
  }

  deleteStep(id: string, stepId: string): Observable<void> { return this.http.delete<void>(`${BASE}/${id}/steps/${stepId}`); }

  reorderSteps(id: string, orderedStepIds: string[]): Observable<Step[]> {
    return this.http.post<Step[]>(`${BASE}/${id}/steps/reorder`, { orderedStepIds });
  }

  /** Sends exactly one of `leadIds` / `filter`. */
  enroll(id: string, target: EnrollTarget, dryRun: boolean): Observable<EnrollResult> {
    const body = target.leadIds ? { leadIds: target.leadIds, dryRun } : { filter: target.filter, dryRun };
    return this.http.post<EnrollResult>(`${BASE}/${id}/enroll`, body);
  }

  enrollments(id: string, status: EnrollmentStatus | null, page: number, pageSize: number): Observable<PagedResult<EnrollmentRow>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (status) params = params.set('status', status);
    return this.http.get<PagedResult<EnrollmentRow>>(`${BASE}/${id}/enrollments`, { params });
  }

  activate(id: string): Observable<Campaign> { return this.http.post<Campaign>(`${BASE}/${id}/activate`, null).pipe(map(normalise)); }

  pause(id: string): Observable<Campaign> { return this.http.post<Campaign>(`${BASE}/${id}/pause`, null).pipe(map(normalise)); }

  activationPreview(id: string): Observable<ActivationPreview> {
    return this.http.get<ActivationPreview>(`${BASE}/${id}/activation-preview`);
  }

  preview(id: string, leadId: string, stepId: string): Observable<PreviewResponse> {
    return this.http.post<PreviewResponse>(`${BASE}/${id}/preview`, { leadId, stepId });
  }

  senderStatus(): Observable<SenderStatus> { return this.http.get<SenderStatus>(`${BASE}/sender`); }
}

function normalise(c: Campaign): Campaign {
  return {
    ...c,
    sendDays: parseSendDays(c.sendDays as unknown as string | number),
    steps: [...(c.steps ?? [])].sort((a, b) => a.order - b.order)
  };
}
