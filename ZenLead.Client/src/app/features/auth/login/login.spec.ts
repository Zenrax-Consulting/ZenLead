import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { Login } from './login';

describe('Login', () => {
  const build = (login: AuthService['login'], home = '/leads') => {
    const auth = { login, homeRoute: () => home } as unknown as AuthService;
    const router = { navigate: vi.fn().mockResolvedValue(true) } as unknown as Router;
    const component = new Login(new FormBuilder(), auth, router, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, router };
  };

  it('does not submit an invalid form', () => {
    const login = vi.fn();
    const { component } = build(login);

    component.submit();

    expect(login).not.toHaveBeenCalled();
  });

  it('navigates to /leads on success', () => {
    const { component, router } = build(vi.fn().mockReturnValue(of({ accessToken: 'a', refreshToken: 'r' })));
    component.form.setValue({ email: 'a@acme.com', password: 'Passw0rd!' });

    component.submit();

    expect(router.navigate).toHaveBeenCalledWith(['/leads']);
  });

  it('sends the super admin to the admin area on success', () => {
    const { component, router } = build(vi.fn().mockReturnValue(of({ accessToken: 'a', refreshToken: 'r' })), '/admin/workspaces');
    component.form.setValue({ email: 'root@zenlead.test', password: 'Passw0rd!' });

    component.submit();

    expect(router.navigate).toHaveBeenCalledWith(['/admin/workspaces']);
  });

  it('shows an error and re-enables the form on failure', () => {
    const { component, router } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 401 }))));
    component.form.setValue({ email: 'a@acme.com', password: 'wrong' });

    component.submit();

    expect(component.errorMessage).toBe('Invalid email or password.');
    expect(component.submitting).toBe(false);
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('explains an account awaiting approval', () => {
    const { component } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 403, error: { code: 'pending_approval' } }))));
    component.form.setValue({ email: 'a@acme.com', password: 'Passw0rd!' });

    component.submit();

    expect(component.errorMessage).toBe('Your account is awaiting approval by your workspace administrator.');
  });

  it('explains a registration that was not approved', () => {
    expect(Login.messageFor({ status: 403, error: { code: 'not_approved' } }))
      .toBe("Your registration wasn't approved. Contact your workspace administrator.");
  });

  it('keeps the generic message for a 403 without a known code', () => {
    expect(Login.messageFor({ status: 403, error: null })).toBe('Invalid email or password.');
  });
});
