import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { of, throwError } from 'rxjs';
import { LeadDetail } from './lead-detail';
import { LeadsService } from '../leads.service';
import { Lead } from '../leads.models';

const lead: Lead = {
  id: 'l1', name: 'Jane', email: 'jane@acme.com', title: 'CTO', status: 'New', createdAt: '2026-10-07T00:00:00Z'
};

describe('LeadDetail', () => {
  const build = (service: Partial<LeadsService>) => {
    const route = { snapshot: { paramMap: { get: () => 'l1' } } } as unknown as ActivatedRoute;
    const component = new LeadDetail(route, service as LeadsService, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    component.ngOnInit();
    return component;
  };

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
});
