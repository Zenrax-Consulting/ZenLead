import { FormBuilder } from '@angular/forms';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { Login } from './login';

describe('Login', () => {
  const build = (login: AuthService['login']) => {
    const auth = { login } as unknown as AuthService;
    const router = { navigate: vi.fn().mockResolvedValue(true) } as unknown as Router;
    const component = new Login(new FormBuilder(), auth, router);
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

  it('shows an error and re-enables the form on failure', () => {
    const { component, router } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 401 }))));
    component.form.setValue({ email: 'a@acme.com', password: 'wrong' });

    component.submit();

    expect(component.errorMessage).toBe('Invalid email or password.');
    expect(component.submitting).toBe(false);
    expect(router.navigate).not.toHaveBeenCalled();
  });
});
