import { ChangeDetectorRef, Component } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { MatDialogRef } from '@angular/material/dialog';
import { AdminService } from '../admin.service';
import { WorkspaceAdmin } from '../admin.models';

/** IANA zones from the browser, always including UTC (the default). */
export function supportedTimeZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  const zones = intl.supportedValuesOf?.('timeZone') ?? [];
  return zones.includes('UTC') ? zones : ['UTC', ...zones];
}

@Component({
  selector: 'app-create-workspace-dialog',
  standalone: false,
  templateUrl: './create-workspace-dialog.html',
  styleUrl: './create-workspace-dialog.css'
})
export class CreateWorkspaceDialog {
  readonly timeZones = supportedTimeZones();
  form: FormGroup;
  submitting = false;
  errorMessage: string | null = null;

  constructor(
    fb: FormBuilder,
    private admin: AdminService,
    private ref: MatDialogRef<CreateWorkspaceDialog, WorkspaceAdmin | undefined>,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.group({
      name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      adminEmail: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
      timeZone: ['UTC']
    });
  }

  submit(): void {
    if (this.form.invalid || this.submitting) return;
    this.submitting = true;
    this.errorMessage = null;
    const v = this.form.getRawValue();

    this.admin.create({ name: v.name.trim(), adminEmail: v.adminEmail.trim(), timeZone: v.timeZone || null }).subscribe({
      next: created => this.ref.close(created),
      error: err => {
        this.submitting = false;
        this.errorMessage = err.status === 409
          ? 'A workspace with that name already exists.'
          : err.status === 400 ? 'Check the name and admin email.' : 'Could not create the workspace.';
        this.cdr.markForCheck();
      }
    });
  }
}
