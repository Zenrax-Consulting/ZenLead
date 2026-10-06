# Feature 8 — Angular Lead-Detail & Draft Generation UI

**Branch:** `feature/angular-lead-detail`
**Milestone:** 2 — AI loop
**Depends on:** Feature 7 (real compose-email endpoint).

## Goal
From a lead's detail page, clicking "Generate draft" calls the real compose-email endpoint and shows the result, with loading/error states surfaced in the UI (not just the console).

## Files to modify/add

### `ZenLead.Client/src/app/features/leads/leads.models.ts` (modified — append)
```typescript
export interface ComposedEmail {
  subject: string;
  body: string;
  tokensUsed: number;
}
```

### `ZenLead.Client/src/app/features/leads/leads.service.ts` (modified — append methods)
```typescript
getById(id: string): Observable<Lead> {
  return this.http.get<Lead>(`/api/v1/leads/${id}`);
}

composeEmail(leadId: string, context?: string): Observable<ComposedEmail> {
  return this.http.post<ComposedEmail>('/api/v1/ai/compose-email', { leadId, context });
}
```
(Import `ComposedEmail` alongside the existing `Lead`/`CreateLeadRequest` import.)

### `ZenLead.Client/src/app/features/leads/lead-detail/lead-detail.ts` (new)
```typescript
import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ComposedEmail, Lead } from '../leads.models';
import { LeadsService } from '../leads.service';

type ComposeState = 'idle' | 'loading' | 'error' | 'done';

@Component({
  selector: 'app-lead-detail',
  standalone: false,
  templateUrl: './lead-detail.html',
  styleUrl: './lead-detail.css'
})
export class LeadDetail implements OnInit {
  lead: Lead | null = null;
  loadError = false;

  draft: ComposedEmail | null = null;
  composeState: ComposeState = 'idle';
  composeErrorMessage: string | null = null;

  constructor(private route: ActivatedRoute, private leadsService: LeadsService) {}

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.leadsService.getById(id).subscribe({
      next: lead => this.lead = lead,
      error: () => this.loadError = true
    });
  }

  generateDraft(): void {
    if (!this.lead) return;
    this.composeState = 'loading';
    this.composeErrorMessage = null;

    this.leadsService.composeEmail(this.lead.id).subscribe({
      next: draft => { this.draft = draft; this.composeState = 'done'; },
      error: err => {
        this.composeState = 'error';
        this.composeErrorMessage = err.status === 504
          ? 'The AI provider timed out. Please try again.'
          : 'Failed to generate a draft.';
      }
    });
  }
}
```

### `ZenLead.Client/src/app/features/leads/lead-detail/lead-detail.html` (new)
```html
<p *ngIf="loadError">Lead not found.</p>

<ng-container *ngIf="lead">
  <h1>{{ lead.name }}</h1>
  <p>{{ lead.email }} <span *ngIf="lead.title">· {{ lead.title }}</span></p>

  <button mat-raised-button color="primary" (click)="generateDraft()" [disabled]="composeState === 'loading'">
    Generate draft
  </button>

  <mat-spinner *ngIf="composeState === 'loading'" diameter="24"></mat-spinner>
  <p *ngIf="composeState === 'error'" class="compose-error">{{ composeErrorMessage }}</p>

  <mat-card *ngIf="draft" class="draft-card">
    <mat-form-field appearance="outline">
      <mat-label>Subject</mat-label>
      <textarea matInput [value]="draft.subject" rows="1"></textarea>
    </mat-form-field>
    <mat-form-field appearance="outline">
      <mat-label>Body</mat-label>
      <textarea matInput [value]="draft.body" rows="8"></textarea>
    </mat-form-field>
  </mat-card>
</ng-container>
```
Textareas are one-way bound (`[value]`, not `[(ngModel)]`/form control) — editable in the browser but intentionally not wired back to any save action, matching the parent plan's "editable textareas, not persisted yet; sending is Sprint 3."

### `ZenLead.Client/src/app/features/leads/leads-list/leads-list.html` (modified — make the name cell a link)
```html
<ng-container matColumnDef="name">
  <th mat-header-cell *matHeaderCellDef>Name</th>
  <td mat-cell *matCellDef="let lead"><a [routerLink]="['/leads', lead.id]">{{ lead.name }}</a></td>
</ng-container>
```

### `ZenLead.Client/src/app/app-module.ts` (modified — add declaration/import)
```typescript
// add to existing imports:
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { LeadDetail } from './features/leads/lead-detail/lead-detail';

// declarations: [..., LeadDetail]
// imports: [..., MatProgressSpinnerModule]
```

### `ZenLead.Client/src/app/app-routing-module.ts` (modified — add the detail route)
```typescript
import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { LeadsList } from './features/leads/leads-list/leads-list';
import { LeadDetail } from './features/leads/lead-detail/lead-detail';
import { authGuard } from './core/auth/auth.guard';

const routes: Routes = [
  { path: '', redirectTo: 'login', pathMatch: 'full' },
  { path: 'register', component: Register },
  { path: 'login', component: Login },
  { path: 'leads', component: LeadsList, canActivate: [authGuard] },
  { path: 'leads/:id', component: LeadDetail, canActivate: [authGuard] }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
```

## Verification
- `ng build` succeeds.
- From `/leads`, clicking a lead's name navigates to `/leads/:id` and shows its fields.
- "Generate draft" shows a spinner, then the subject/body panel on success.
- Killing the backend (or an invalid `OpenAI:ApiKey`) while clicking "Generate draft" surfaces the friendly timeout/error message in the UI, not a silent console-only failure.
