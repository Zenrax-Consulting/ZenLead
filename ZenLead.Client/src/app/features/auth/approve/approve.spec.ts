import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApprovalInfo } from '../../../core/auth/auth.models';
import { ApprovalsService } from '../../../core/auth/approvals.service';
import { Approve } from './approve';

describe('Approve', () => {
  const pending: ApprovalInfo = { status: 'pending', requesterName: 'Colin', requesterEmail: 'colin@zenrax.test', workspaceName: 'Zenrax' };

  const build = (token: string | null, info = vi.fn().mockReturnValue(of(pending)), decide = vi.fn().mockReturnValue(of(undefined))) => {
    const route = { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } } as unknown as ActivatedRoute;
    const router = { navigate: vi.fn().mockResolvedValue(true) } as unknown as Router;
    const approvals = { info, decide } as unknown as ApprovalsService;
    const component = new Approve(route, router, approvals, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, router, info, decide, route };
  };

  it('strips the token from the address bar and loads the request', () => {
    const { component, router, info, route } = build('tok');

    component.ngOnInit();

    expect(router.navigate).toHaveBeenCalledWith([], { relativeTo: route, replaceUrl: true, queryParams: {} });
    expect(info).toHaveBeenCalledWith('tok');
    expect(component.state).toBe('ready');
    expect(component.info).toEqual(pending);
  });

  it('never decides just by opening the page', () => {
    const { component, decide } = build('tok');
    component.ngOnInit();
    expect(decide).not.toHaveBeenCalled();
  });

  it('is invalid without a token', () => {
    const { component, info } = build(null);
    component.ngOnInit();
    expect(component.state).toBe('invalid');
    expect(info).not.toHaveBeenCalled();
  });

  it.each([
    [{ ...pending, status: 'used' as const }, 'used'],
    [{ ...pending, status: 'expired' as const }, 'expired']
  ])('shows %o as %s', (response, state) => {
    const { component } = build('tok', vi.fn().mockReturnValue(of(response)));
    component.ngOnInit();
    expect(component.state).toBe(state);
  });

  it('maps a 404 on load to invalid and anything else to error', () => {
    const notFound = build('tok', vi.fn().mockReturnValue(throwError(() => ({ status: 404 }))));
    notFound.component.ngOnInit();
    expect(notFound.component.state).toBe('invalid');

    const broken = build('tok', vi.fn().mockReturnValue(throwError(() => ({ status: 500 }))));
    broken.component.ngOnInit();
    expect(broken.component.state).toBe('error');
  });

  it('approves and rejects with the token read on load', () => {
    const approve = build('tok');
    approve.component.ngOnInit();
    approve.component.decide('approve');
    expect(approve.decide).toHaveBeenCalledWith('tok', 'approve');
    expect(approve.component.state).toBe('approved');

    const reject = build('tok');
    reject.component.ngOnInit();
    reject.component.decide('reject');
    expect(reject.component.state).toBe('rejected');
  });

  it('maps decision errors', () => {
    expect(Approve.stateForError(409)).toBe('used');
    expect(Approve.stateForError(410)).toBe('expired');
    expect(Approve.stateForError(404)).toBe('invalid');
    expect(Approve.stateForError(500)).toBe('error');

    const { component } = build('tok', undefined, vi.fn().mockReturnValue(throwError(() => ({ status: 409 }))));
    component.ngOnInit();
    component.decide('approve');
    expect(component.state).toBe('used');
    expect(component.deciding).toBe(false);
  });
});
