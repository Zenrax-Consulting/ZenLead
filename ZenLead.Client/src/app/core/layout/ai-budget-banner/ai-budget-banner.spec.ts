import { ChangeDetectorRef } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { AiBudget, AiUsageService } from '../../ai/ai-usage.service';
import { AiBudgetBanner } from './ai-budget-banner';

describe('AiBudgetBanner', () => {
  const budget = (state: AiBudget['state']): AiBudget => ({ spentUsd: 1, capUsd: 20, state, resetsOn: '2026-12-01' });

  const build = () => {
    const subject = new BehaviorSubject<AiBudget | null>(null);
    const service = { budget$: subject.asObservable(), refresh: vi.fn() } as unknown as AiUsageService;
    const component = new AiBudgetBanner(service, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    component.ngOnInit();
    return { component, subject, service };
  };

  it('refreshes the budget when the shell opens', () => {
    const { service } = build();
    expect(service.refresh).toHaveBeenCalled();
  });

  it('shows nothing until the budget is known, or while it is Ok', () => {
    const { component, subject } = build();
    expect(component.message).toBeNull();
    subject.next(budget('Ok'));
    expect(component.message).toBeNull();
  });

  it('warns at 80%', () => {
    const { component, subject } = build();
    subject.next(budget('Warning'));
    expect(component.message).toBe("80% of this month's AI budget used.");
  });

  it('says drafts are paused until the reset date when exceeded', () => {
    const { component, subject } = build();
    subject.next(budget('Exceeded'));
    expect(component.message).toBe('AI drafts are paused until 1 December 2026.');
  });

  it('formats the reset date without time-zone drift', () => {
    expect(AiBudgetBanner.formatDate('2027-01-01')).toBe('1 January 2027');
  });
});
