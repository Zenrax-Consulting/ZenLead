import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { ChangePassword } from './change-password';

describe('ChangePassword', () => {
  const build = (changePassword = vi.fn().mockReturnValue(of({ accessToken: 'a', refreshToken: 'r' }))) => {
    const auth = { changePassword } as unknown as AuthService;
    const snackBar = { open: vi.fn() } as unknown as MatSnackBar;
    const component = new ChangePassword(new FormBuilder(), auth, snackBar, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, changePassword, snackBar };
  };

  const fill = (c: ChangePassword) => c.form.setValue({ currentPassword: 'Old!Passw0rd', newPassword: 'N3w!Passw0rd', confirm: 'N3w!Passw0rd' });

  it('changes the password and confirms with a snackbar', () => {
    const { component, changePassword, snackBar } = build();
    fill(component);

    component.submit();

    expect(changePassword).toHaveBeenCalledWith('Old!Passw0rd', 'N3w!Passw0rd');
    expect(snackBar.open).toHaveBeenCalledWith('Password changed. Other devices were signed out.', 'OK', { duration: 4000 });
    expect(component.form.value.currentPassword).toBeFalsy();
  });

  it('does not submit when the confirmation does not match', () => {
    const { component, changePassword } = build();
    component.form.setValue({ currentPassword: 'Old!Passw0rd', newPassword: 'N3w!Passw0rd', confirm: 'nope' });
    component.submit();
    expect(changePassword).not.toHaveBeenCalled();
  });

  it('maps wrong_password to a plain message', () => {
    const { component } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 400, error: { code: 'wrong_password' } }))));
    fill(component);
    component.submit();
    expect(component.errorMessage).toBe('The current password is incorrect.');
  });

  it('shows the server policy messages', () => {
    const { component } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 400, error: { errors: { NewPassword: ['Too weak.'] } } }))));
    fill(component);
    component.submit();
    expect(component.errorMessage).toBe('Too weak.');
  });
});
