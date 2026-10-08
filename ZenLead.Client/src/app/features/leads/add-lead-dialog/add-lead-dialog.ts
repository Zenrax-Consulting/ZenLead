import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialogRef } from '@angular/material/dialog';
import { LeadsService } from '../leads.service';

@Component({
  selector: 'app-add-lead-dialog',
  standalone: false,
  templateUrl: './add-lead-dialog.html',
  styleUrl: './add-lead-dialog.css'
})
export class AddLeadDialog {
  form: FormGroup;
  submitting = false;
  errorMessage: string | null = null;

  constructor(
    private fb: FormBuilder,
    private leads: LeadsService,
    private ref: MatDialogRef<AddLeadDialog, boolean>,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.group({
      name: ['', Validators.required],
      email: ['', [Validators.required, Validators.email]],
      title: [''],
      companyName: [''],
      companyDomain: ['']
    });
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;
    const v = this.form.getRawValue();

    this.leads.create({
      name: v.name, email: v.email, title: v.title || null,
      companyName: v.companyName || null, companyDomain: v.companyDomain || null
    }).subscribe({
      next: () => this.ref.close(true),
      error: err => {
        this.submitting = false;
        this.errorMessage = err.status === 409
          ? (err.error?.message ?? 'This lead already exists or has been suppressed.')
          : 'Failed to add lead.';
        this.cdr.markForCheck();
      }
    });
  }
}
