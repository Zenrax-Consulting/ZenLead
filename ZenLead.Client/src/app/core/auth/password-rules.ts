import { AbstractControl, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';

/** Client-side mirror of the server's PasswordRules (length 8 + digit, lower, upper, symbol). The API stays the authority. */
export const strongPasswordValidators: ValidatorFn[] = [
  Validators.required,
  Validators.minLength(8),
  Validators.pattern(/[0-9]/),
  Validators.pattern(/[a-z]/),
  Validators.pattern(/[A-Z]/),
  Validators.pattern(/[^a-zA-Z0-9]/)
];

export const passwordHint = 'At least 8 characters, with upper and lower case letters, a digit and a symbol.';

/** Group validator: `newPassword` and `confirm` must match. */
export function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  const next = group.get('newPassword')?.value as string | null;
  const confirm = group.get('confirm')?.value as string | null;
  return next && confirm && next !== confirm ? { mismatch: true } : null;
}

/** Turns an API error into one line: validation messages if any, otherwise a fallback. */
export function passwordErrorMessage(err: { status?: number; error?: { errors?: Record<string, string[]>; message?: string } | null }, fallback: string): string {
  if (err.status === 429) return 'Too many requests. Try again later.';
  if (err.status === 400 && err.error?.errors) {
    const messages = Object.values(err.error.errors).flat();
    if (messages.length) return messages.join(' ');
  }
  return err.error?.message ?? fallback;
}
