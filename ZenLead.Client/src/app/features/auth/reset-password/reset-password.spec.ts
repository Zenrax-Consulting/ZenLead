import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { ResetPassword } from './reset-password';

describe('ResetPassword', () => {
  const build = (token: string | null, resetPassword = vi.fn().mockReturnValue(of(undefined))) => {
    const route = { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } } as unknown as ActivatedRoute;
    const router = { navigate: vi.fn().mockResolvedValue(true) } as unknown as Router;
    const auth = { resetPassword, logout: vi.fn() } as unknown as AuthService;
    const component = new ResetPassword(new FormBuilder(), route, router, auth, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, router, route, auth, resetPassword };
  };

  it('reads the token, then removes it from the address bar and history', () => {
    const { component, router, route, resetPassword } = build('tok');

    component.ngOnInit();

    expect(router.navigate).toHaveBeenCalledWith([], { relativeTo: route, replaceUrl: true, queryParams: {} });
    expect(component.state).toBe('form');

    component.form.setValue({ newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rd' });
    component.submit();
    expect(resetPassword).toHaveBeenCalledWith('tok', 'N3w!Passw0rd');
  });

  it('is invalid without a token', () => {
    const { component } = build(null);
    component.ngOnInit();
    expect(component.state).toBe('invalid');
  });

  it('flags mismatched passwords and does not submit', () => {
    const { component, resetPassword } = build('tok');
    component.ngOnInit();
    component.form.setValue({ newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rX' });

    expect(component.form.hasError('mismatch')).toBe(true);
    component.submit();
    expect(resetPassword).not.toHaveBeenCalled();
  });

  it('applies the same strength rules as the server', () => {
    const { component } = build('tok');
    for (const weak of ['short1!', 'password1!', 'PASSWORD1!', 'Password!!', 'Password12']) {
      component.form.setValue({ newPassword: weak, confirm: weak });
      expect(component.form.valid).toBe(false);
    }
    component.form.setValue({ newPassword: 'Passw0rd!', confirm: 'Passw0rd!' });
    expect(component.form.valid).toBe(true);
  });

  it('shows done and drops any stale session on success', () => {
    const { component, auth } = build('tok');
    component.ngOnInit();
    component.form.setValue({ newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rd' });

    component.submit();

    expect(component.state).toBe('done');
    expect(auth.logout).toHaveBeenCalled();
  });

  it('switches to the invalid-link state on invalid_token', () => {
    const { component } = build('tok', vi.fn().mockReturnValue(throwError(() => ({ status: 400, error: { code: 'invalid_token' } }))));
    component.ngOnInit();
    component.form.setValue({ newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rd' });

    component.submit();

    expect(component.state).toBe('invalid');
  });

  it('stays on the form with the policy messages for a weak password (the link still works)', () => {
    const error = { status: 400, error: { errors: { NewPassword: ['Passwords must have at least one digit.'] } } };
    const { component, resetPassword } = build('tok', vi.fn().mockReturnValueOnce(throwError(() => error)).mockReturnValue(of(undefined)));
    component.ngOnInit();
    component.form.setValue({ newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rd' });

    component.submit();
    expect(component.state).toBe('form');
    expect(component.errorMessage).toBe('Passwords must have at least one digit.');

    component.submit();
    expect(resetPassword).toHaveBeenLastCalledWith('tok', 'N3w!Passw0rd');
    expect(component.state).toBe('done');
  });
});
