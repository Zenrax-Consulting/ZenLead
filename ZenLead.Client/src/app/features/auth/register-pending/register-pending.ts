import { Component } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/** Shown after a registration request: nothing to do until the workspace admin approves it. */
@Component({
  selector: 'app-register-pending',
  standalone: false,
  template: `
    <mat-card class="auth-card">
      <mat-card-title>Request sent</mat-card-title>
      <p>
        You'll be able to sign in once the admin of
        @if (workspace) { <strong>{{ workspace }}</strong> } @else { your workspace }
        approves it. We'll email you.
      </p>
      <p class="auth-switch"><a routerLink="/login">Back to sign in</a></p>
    </mat-card>
  `,
  styleUrl: '../register/register.css'
})
export class RegisterPending {
  readonly workspace: string | null;

  constructor(route: ActivatedRoute) {
    this.workspace = route.snapshot.queryParamMap.get('w');
  }
}
