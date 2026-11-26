import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder, FormControl } from '@angular/forms';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { WorkspacePickerService } from '../../../core/workspace-picker.service';
import { Register, requiredOption } from './register';

describe('Register.messageFor', () => {
  it('explains rate limiting on 429', () => {
    expect(Register.messageFor({ status: 429 })).toBe('Too many requests, try again later.');
  });

  it('explains a workspace that is not accepting registrations on 409', () => {
    expect(Register.messageFor({ status: 409 })).toBe("That workspace isn't accepting registrations yet.");
  });

  it('surfaces server validation messages on 400', () => {
    const message = Register.messageFor({
      status: 400,
      error: { errors: { Password: ['Password must contain a digit.', 'Password must contain an uppercase letter.'] } }
    });
    expect(message).toBe('Password must contain a digit. Password must contain an uppercase letter.');
  });

  it('falls back to a generic message otherwise', () => {
    expect(Register.messageFor({ status: 500 })).toBe('Registration failed. Please try again.');
  });
});

describe('requiredOption', () => {
  it('rejects free text and empty values', () => {
    expect(requiredOption(new FormControl('Zenrax'))).toEqual({ requiredOption: true });
    expect(requiredOption(new FormControl(null))).toEqual({ requiredOption: true });
  });

  it('accepts an option picked from the list', () => {
    expect(requiredOption(new FormControl({ id: 'w1', name: 'Zenrax' }))).toBeNull();
  });
});

describe('Register', () => {
  const zenrax = { id: 'w1', name: 'Zenrax' };

  const build = (register = vi.fn().mockReturnValue(of(undefined)), search = vi.fn().mockReturnValue(of([zenrax]))) => {
    const auth = { register } as unknown as AuthService;
    const picker = { search } as unknown as WorkspacePickerService;
    const router = { navigate: vi.fn().mockResolvedValue(true) } as unknown as Router;
    const component = new Register(new FormBuilder(), auth, picker, router, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, router, register, search };
  };

  const fill = (component: Register, workspace: unknown) =>
    component.form.setValue({ workspace, displayName: 'Colin', email: 'colin@zenrax.test', password: 'Passw0rd!' });

  it('cannot submit with typed text instead of a picked workspace', () => {
    const { component, register } = build();
    fill(component, 'Zenrax');

    component.submit();

    expect(component.form.invalid).toBe(true);
    expect(register).not.toHaveBeenCalled();
  });

  it('sends the picked workspace id and goes to the pending screen', () => {
    const { component, register, router } = build();
    fill(component, zenrax);

    component.submit();

    expect(register).toHaveBeenCalledWith({ email: 'colin@zenrax.test', password: 'Passw0rd!', displayName: 'Colin', workspaceId: 'w1' });
    expect(router.navigate).toHaveBeenCalledWith(['/register/pending'], { queryParams: { w: 'Zenrax' } });
  });

  it('shows the server message and re-enables the form on failure', () => {
    const { component, router } = build(vi.fn().mockReturnValue(throwError(() => ({ status: 409 }))));
    fill(component, zenrax);

    component.submit();

    expect(component.errorMessage).toBe("That workspace isn't accepting registrations yet.");
    expect(component.submitting).toBe(false);
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('searches for typed text but not for a picked option', () => {
    const { component, search } = build();
    const values = new Subject<unknown>();
    const results: unknown[] = [];
    component.suggestions(values, 0).subscribe(r => results.push(r));

    vi.useFakeTimers();
    values.next('zen');
    vi.advanceTimersByTime(1);
    values.next(zenrax);
    vi.advanceTimersByTime(1);
    vi.useRealTimers();

    expect(search).toHaveBeenCalledTimes(1);
    expect(search).toHaveBeenCalledWith('zen');
    expect(results).toEqual([[zenrax]]);
  });

  it('shows the option name in the input', () => {
    const { component } = build();
    expect(component.displayWith(zenrax)).toBe('Zenrax');
    expect(component.displayWith('typed')).toBe('typed');
    expect(component.displayWith(null)).toBe('');
  });
});
