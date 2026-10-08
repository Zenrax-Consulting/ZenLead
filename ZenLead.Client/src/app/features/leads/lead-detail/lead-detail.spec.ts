import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { LeadSelectionService } from '../lead-selection.service';
import { LeadDetail } from './lead-detail';
import { LeadsService } from '../leads.service';
import { Lead } from '../leads.models';

const lead: Lead = {
  id: 'l1', name: 'Jane', email: 'jane@acme.com', title: 'CTO', status: 'New', createdAt: '2026-10-07T00:00:00Z',
  company: null, source: 'Manual', sourceRunId: null, emailVerificationStatus: 'Unverified'
};

describe('LeadDetail', () => {
  const navigate = vi.fn();
  const confirmResult = { value: true };
  const dialog = { open: () => ({ afterClosed: () => of(confirmResult.value) }) };

  const build = (service: Partial<LeadsService>) => {
    const route = { snapshot: { paramMap: { get: () => 'l1' } } } as unknown as ActivatedRoute;
    const component = new LeadDetail(
      route, service as LeadsService, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef,
      new FormBuilder(), dialog as unknown as MatDialog, { navigate } as unknown as Router, new LeadSelectionService());
    component.ngOnInit();
    return component;
  };

  beforeEach(() => { navigate.mockClear(); confirmResult.value = true; });

  it('loads the lead', () => {
    const component = build({ getById: () => of(lead) });
    expect(component.lead).toEqual(lead);
  });

  it('flags a lead that cannot be loaded', () => {
    const component = build({ getById: () => throwError(() => ({ status: 404 })) });
    expect(component.loadError).toBe(true);
  });

  it('shows the draft when composing succeeds', () => {
    const draft = { subject: 'Hi', body: 'Body', tokensUsed: 10 };
    const component = build({ getById: () => of(lead), composeEmail: () => of(draft) });

    component.generateDraft();

    expect(component.composeState).toBe('done');
    expect(component.draft).toEqual(draft);
  });

  it.each([
    [429, 'Too many draft requests'],
    [502, 'unusable draft'],
    [503, 'currently unavailable'],
    [504, 'timed out'],
    [500, 'Failed to generate a draft.']
  ])('shows a friendly message for a %i from the compose endpoint', (status, fragment) => {
    const component = build({ getById: () => of(lead), composeEmail: () => throwError(() => ({ status })) });

    component.generateDraft();

    expect(component.composeState).toBe('error');
    expect(component.composeErrorMessage).toContain(fragment);
  });

  it('saves an edit via PUT and shows the result', () => {
    const saved = { ...lead, name: 'Jane Q', status: 'Contacted' as const };
    const update = vi.fn(() => of(saved));
    const component = build({ getById: () => of(lead), update });

    component.startEdit();
    component.form.patchValue({ name: 'Jane Q', status: 'Contacted' });
    component.save();

    expect(update).toHaveBeenCalledWith('l1', { name: 'Jane Q', title: 'CTO', status: 'Contacted', companyName: null, companyDomain: null });
    expect(component.lead).toEqual(saved);
    expect(component.editing).toBe(false);
  });

  it('shows the server message when the edit is rejected with a 409', () => {
    const component = build({
      getById: () => of(lead),
      update: () => throwError(() => ({ status: 409, error: { message: 'Cannot change status from Bounced to New.' } }))
    });

    component.startEdit();
    component.save();

    expect(component.saveError).toBe('Cannot change status from Bounced to New.');
    expect(component.editing).toBe(true);
  });

  it('offers only legal next statuses while editing', () => {
    const component = build({ getById: () => of({ ...lead, status: 'Replied' }) });
    expect(component.statusOptions).toEqual(['Replied', 'Unsubscribed', 'Bounced']);
  });

  it('deletes after confirmation and returns to the list', () => {
    const del = vi.fn(() => of(undefined));
    const component = build({ getById: () => of(lead), delete: del });

    component.confirmDelete();

    expect(del).toHaveBeenCalledWith('l1');
    expect(navigate).toHaveBeenCalledWith(['/leads'], { queryParams: {} });
  });

  it('does nothing when the delete confirmation is dismissed', () => {
    confirmResult.value = false;
    const del = vi.fn(() => of(undefined));
    const component = build({ getById: () => of(lead), delete: del });

    component.confirmDelete();

    expect(del).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('labels the source, and only a run-backed lead has a run to link to', () => {
    const csv = build({ getById: () => of({ ...lead, source: 'Csv' }) });
    const run = build({ getById: () => of({ ...lead, source: 'Discovery', sourceRunId: 'r1' }) });

    expect(csv.sourceLabel).toBe('CSV import');
    expect(csv.lead?.sourceRunId).toBeNull();
    expect(run.sourceLabel).toBe('Discovery run');
    expect(run.lead?.sourceRunId).toBe('r1');
  });
});
