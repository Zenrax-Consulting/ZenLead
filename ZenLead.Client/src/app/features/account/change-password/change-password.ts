import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, FormGroupDirective, Validators } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../../core/auth/auth.service';
import { passwordErrorMessage, passwordHint, passwordsMatch, strongPasswordValidators } from '../../../core/auth/password-rules';

@Component({
  selector: 'app-change-password',
  standalone: false,
  templateUrl: './change-password.html',
  styleUrl: './change-password.css'
})
export class ChangePassword {
  form: FormGroup;
  submitting = false;
  errorMessage: string | null = null;
  readonly hint = passwordHint;

  constructor(fb: FormBuilder, private auth: AuthService, private snackBar: MatSnackBar, private cdr: ChangeDetectorRef) {
    this.form = fb.group(
      { currentPassword: ['', Validators.required], newPassword: ['', strongPasswordValidators], confirm: ['', Validators.required] },
      { validators: passwordsMatch }
    );
  }

  static messageFor(err: { status?: number; error?: { code?: string; errors?: Record<string, string[]> } | null }): string {
    if (err.status === 400 && err.error?.code === 'wrong_password') return 'The current password is incorrect.';
    return passwordErrorMessage(err, "We couldn't change the password. Please try again.");
  }

  submit(formDirective?: FormGroupDirective): void {
    if (this.form.invalid || this.submitting) return;
    this.submitting = true;
    this.errorMessage = null;
    const { currentPassword, newPassword } = this.form.getRawValue() as { currentPassword: string; newPassword: string };

    this.auth.changePassword(currentPassword, newPassword).subscribe({
      next: () => {
        this.submitting = false;
        formDirective ? formDirective.resetForm() : this.form.reset();
        this.snackBar.open('Password changed. Other devices were signed out.', 'OK', { duration: 4000 });
        this.cdr.markForCheck();   // app is zoneless: async state changes must schedule a render
      },
      error: err => {
        this.submitting = false;
        this.errorMessage = ChangePassword.messageFor(err);
        this.cdr.markForCheck();
      }
    });
  }
}
