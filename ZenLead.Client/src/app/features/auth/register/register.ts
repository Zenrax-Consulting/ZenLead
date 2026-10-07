import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-register',
  standalone: false,
  templateUrl: './register.html',
  styleUrl: './register.css'
})
export class Register {
  form: FormGroup;

  submitting = false;
  errorMessage: string | null = null;

  constructor(fb: FormBuilder, private auth: AuthService, private router: Router, private cdr: ChangeDetectorRef) {
    this.form = fb.group({
      workspaceName: ['', Validators.required],
      displayName: ['', Validators.required],
      email: ['', [Validators.required, Validators.email]],
      password: ['', [Validators.required, Validators.minLength(8)]]
    });
  }

  static messageFor(err: { status?: number; error?: { errors?: Record<string, string[]> } }): string {
    if (err.status === 409) return 'That email is already registered. Try logging in instead.';
    if (err.status === 400 && err.error?.errors) {
      const messages = Object.values(err.error.errors).flat();
      if (messages.length) return messages.join(' ');
    }
    return 'Registration failed. Please try again.';
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.register(this.form.getRawValue()).subscribe({
      next: () => this.router.navigate(['/leads']), // register already stores the session
      error: err => {
        this.submitting = false;
        this.errorMessage = Register.messageFor(err);
        this.cdr.markForCheck(); // app is zoneless: async state changes must schedule a render
      }
    });
  }
}
