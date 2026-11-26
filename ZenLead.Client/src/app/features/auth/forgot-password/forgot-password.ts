import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { AuthService } from '../../../core/auth/auth.service';

/**
 * Public "forgot password" page (F21). After any 200 the form is swapped for the same neutral confirmation,
 * so the page never hints whether the address has an account.
 */
@Component({
  selector: 'app-forgot-password',
  standalone: false,
  templateUrl: './forgot-password.html',
  styleUrl: '../register/register.css'
})
export class ForgotPassword {
  static readonly sentMessage = "If an account exists for that address, we've emailed a reset link. Check your inbox — the link works for 1 hour.";

  form: FormGroup;
  sent = false;
  submitting = false;
  errorMessage: string | null = null;
  readonly sentMessage = ForgotPassword.sentMessage;

  constructor(fb: FormBuilder, private auth: AuthService, private cdr: ChangeDetectorRef) {
    this.form = fb.group({ email: ['', [Validators.required, Validators.email]] });
  }

  static messageFor(err: { status?: number }): string {
    return err.status === 429 ? 'Too many requests. Try again later.' : "We couldn't send the request. Please try again.";
  }

  submit(): void {
    if (this.form.invalid || this.submitting) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.forgotPassword((this.form.value.email as string).trim()).subscribe({
      next: () => {
        this.sent = true;
        this.submitting = false;
        this.cdr.markForCheck();   // app is zoneless: async state changes must schedule a render
      },
      error: err => {
        this.submitting = false;
        this.errorMessage = ForgotPassword.messageFor(err);
        this.cdr.markForCheck();
      }
    });
  }
}
