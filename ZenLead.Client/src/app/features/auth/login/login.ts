import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: false,
  templateUrl: './login.html',
  styleUrl: './login.css'
})
export class Login {
  form: FormGroup;

  submitting = false;
  errorMessage: string | null = null;

  constructor(fb: FormBuilder, private auth: AuthService, private router: Router, private cdr: ChangeDetectorRef) {
    this.form = fb.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', Validators.required]
    });
  }

  /** 403 carries a code for accounts that exist but can't sign in (F20); everything else stays the generic message. */
  static messageFor(err: { status?: number; error?: { code?: string } | null }): string {
    if (err.status === 403 && err.error?.code === 'pending_approval')
      return 'Your account is awaiting approval by your workspace administrator.';
    if (err.status === 403 && err.error?.code === 'not_approved')
      return "Your registration wasn't approved. Contact your workspace administrator.";
    return 'Invalid email or password.';
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => this.router.navigate([this.auth.homeRoute()]),
      error: err => {
        this.submitting = false;
        this.errorMessage = Login.messageFor(err);
        this.cdr.markForCheck(); // app is zoneless: async state changes must schedule a render
      }
    });
  }
}
