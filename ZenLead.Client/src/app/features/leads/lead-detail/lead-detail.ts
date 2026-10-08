import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { ConfirmDialog } from '../confirm-dialog/confirm-dialog';
import { LeadSelectionService } from '../lead-selection.service';
import { allowedStatuses } from '../lead-status';
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

  editing = false;
  saving = false;
  saveError: string | null = null;
  deleteError: string | null = null;
  form: FormGroup;

  constructor(
    private route: ActivatedRoute,
    private leadsService: LeadsService,
    private cdr: ChangeDetectorRef,
    private fb: FormBuilder,
    private dialog: MatDialog,
    private router: Router,
    private selection: LeadSelectionService
  ) {
    this.form = fb.group({
      name: ['', Validators.required],
      title: [''],
      status: ['New'],
      companyName: [''],
      companyDomain: ['']
    });
  }

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.leadsService.getById(id).subscribe({
      // app is zoneless: async state changes must schedule a render
      next: lead => { this.lead = lead; this.cdr.markForCheck(); },
      error: () => { this.loadError = true; this.cdr.markForCheck(); }
    });
  }

  static composeErrorFor(status: number): string {
    switch (status) {
      case 400: return 'The request was invalid. Please shorten any extra context and try again.';
      case 429: return 'Too many draft requests right now. Please wait a minute and try again.';
      case 502: return 'The AI returned an unusable draft. Please try again.';
      case 503: return 'The AI provider is currently unavailable. Please try again later.';
      case 504: return 'The AI provider timed out. Please try again.';
      default: return 'Failed to generate a draft.';
    }
  }

  generateDraft(): void {
    if (!this.lead) return;
    this.composeState = 'loading';
    this.composeErrorMessage = null;

    this.leadsService.composeEmail(this.lead.id).subscribe({
      next: draft => { this.draft = draft; this.composeState = 'done'; this.cdr.markForCheck(); },
      error: err => {
        this.composeState = 'error';
        this.composeErrorMessage = LeadDetail.composeErrorFor(err.status);
        this.cdr.markForCheck();
      }
    });
  }

  get statusOptions() { return this.lead ? allowedStatuses(this.lead.status) : []; }

  get sourceLabel(): string {
    switch (this.lead?.source) {
      case 'Csv': return 'CSV import';
      case 'Discovery': return 'Discovery run';
      default: return 'Manual';
    }
  }

  startEdit(): void {
    if (!this.lead) return;
    this.form.reset({
      name: this.lead.name,
      title: this.lead.title ?? '',
      status: this.lead.status,
      companyName: this.lead.company?.name ?? '',
      companyDomain: this.lead.company?.domain ?? ''
    });
    this.saveError = null;
    this.editing = true;
  }

  cancelEdit(): void { this.editing = false; this.saveError = null; }

  save(): void {
    if (!this.lead || this.form.invalid) return;
    this.saving = true;
    this.saveError = null;
    const v = this.form.getRawValue();

    this.leadsService.update(this.lead.id, {
      name: v.name, title: v.title || null, status: v.status,
      companyName: v.companyName || null, companyDomain: v.companyDomain || null
    }).subscribe({
      next: lead => { this.lead = lead; this.editing = false; this.saving = false; this.cdr.markForCheck(); },
      error: err => {
        this.saving = false;
        this.saveError = err.status === 409 ? (err.error?.message ?? 'That change is not allowed.') : 'Failed to save changes.';
        this.cdr.markForCheck();
      }
    });
  }

  confirmDelete(): void {
    if (!this.lead) return;
    const id = this.lead.id;
    this.dialog.open(ConfirmDialog, {
      data: {
        title: 'Delete lead?',
        message: 'This removes the lead from your list. Campaign history is kept.',
        confirmLabel: 'Delete'
      }
    }).afterClosed().subscribe(confirmed => {
      if (!confirmed) return;
      this.deleteError = null;
      this.leadsService.delete(id).subscribe({
        next: () => { this.selection.setMany([id], false); this.backToList(); },
        error: () => { this.deleteError = 'Failed to delete lead.'; this.cdr.markForCheck(); }
      });
    });
  }

  /** Returns to the list with the filter/sort/page it was showing. */
  backToList(): void {
    const q = this.selection.lastQuery;
    const queryParams: Record<string, string | number> = {};
    if (q) {
      if (q.page > 1) queryParams['page'] = q.page;
      if (q.pageSize !== 25) queryParams['pageSize'] = q.pageSize;
      for (const key of ['q', 'status', 'companyId', 'source', 'sourceRunId', 'sort'] as const) {
        if (q[key]) queryParams[key] = q[key]!;
      }
    }
    this.router.navigate(['/leads'], { queryParams });
  }
}
