import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { ForgotPassword } from './forgot-password';

describe('ForgotPassword', () => {
  const build = (forgotPassword = vi.fn().mockReturnValue(of(undefined))) => {
    const auth = { forgotPassword } as unknown as AuthService;
    const component = new ForgotPassword(new FormBuilder(), auth, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, forgotPassword };
  };

  it('does not submit an invalid email', () => {
    const { component, forgotPassword } = build();
    component.form.setValue({ email: 'not-an-email' });
    component.submit();
    expect(forgotPassword).not.toHaveBeenCalled();
  });

  it('shows the same neutral confirmation after any 200', () => {
    const { component, forgotPassword } = build();
    component.form.setValue({ email: 'someone@acme.com' });

    component.submit();

    expect(forgotPassword).toHaveBeenCalledWith('someone@acme.com');
    expect(component.sent).toBe(true);
    expect(component.sentMessage).toContain('If an account exists');
    expect(component.sentMessage).toContain('1 hour');
    expect(component.errorMessage).toBeNull();
  });

  it('gives no per-account hint: the confirmation does not depend on the address', () => {
    const a = build();
    a.component.form.setValue({ email: 'known@acme.com' });
    a.component.submit();
    const b = build();
    b.component.form.setValue({ email: 'nobody@example.com' });
    b.component.submit();

    expect(a.component.sent).toBe(b.component.sent);
    expect(a.component.sentMessage).toBe(b.component.sentMessage);
    expect(a.component.sentMessage).not.toContain('known@acme.com');
  });

  it('says to try later on 429 and keeps the form', () => {
    const { component } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 429 }))));
    component.form.setValue({ email: 'a@acme.com' });

    component.submit();

    expect(component.sent).toBe(false);
    expect(component.errorMessage).toBe('Too many requests. Try again later.');
  });

  it('shows a generic error (never "no such account") on other failures', () => {
    const { component } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 500 }))));
    component.form.setValue({ email: 'a@acme.com' });

    component.submit();

    expect(component.sent).toBe(false);
    expect(component.errorMessage).not.toMatch(/account|exist|found/i);
  });
});
