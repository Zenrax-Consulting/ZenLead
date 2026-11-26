import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../../core/auth/auth.service';
import { passwordErrorMessage, passwordHint, passwordsMatch, strongPasswordValidators } from '../../../core/auth/password-rules';

export type ResetState = 'form' | 'done' | 'invalid';

/**
 * Public page behind the link in the reset email (F21). The token is read once, kept only in this component instance,
 * and removed from the address bar and history straight away.
 */
@Component({
  selector: 'app-reset-password',
  standalone: false,
  templateUrl: './reset-password.html',
  styleUrl: '../register/register.css'
})
export class ResetPassword implements OnInit {
  state: ResetState = 'form';
  form: FormGroup;
  submitting = false;
  errorMessage: string | null = null;
  readonly hint = passwordHint;
  private token: string | null = null;

  constructor(
    fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private auth: AuthService,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.group(
      { newPassword: ['', strongPasswordValidators], confirm: ['', Validators.required] },
      { validators: passwordsMatch }
    );
  }

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token');
    this.router.navigate([], { relativeTo: this.route, replaceUrl: true, queryParams: {} });   // out of the address bar and history
    if (!this.token) this.state = 'invalid';
  }

  submit(): void {
    if (!this.token || this.form.invalid || this.submitting) return;
    this.submitting = true;
    this.errorMessage = null;

    this.auth.resetPassword(this.token, this.form.value.newPassword as string).subscribe({
      next: () => {
        this.auth.logout();          // a stale session in this tab must not linger
        this.token = null;
        this.finish('done');
      },
      error: err => {
        const invalid = err.error?.code === 'invalid_token';
        if (invalid) this.token = null;
        this.errorMessage = invalid ? null : passwordErrorMessage(err, "We couldn't change the password. Please try again.");
        this.finish(invalid ? 'invalid' : 'form');
      }
    });
  }

  private finish(state: ResetState): void {
    this.submitting = false;
    this.state = state;
    this.cdr.markForCheck();   // app is zoneless: async state changes must schedule a render
  }
}
