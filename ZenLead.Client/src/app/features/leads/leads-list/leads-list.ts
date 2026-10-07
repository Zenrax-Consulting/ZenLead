import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Lead } from '../leads.models';
import { LeadsService } from '../leads.service';

@Component({
  selector: 'app-leads-list',
  standalone: false,
  templateUrl: './leads-list.html',
  styleUrl: './leads-list.css'
})
export class LeadsList implements OnInit {
  leads: Lead[] = [];
  loading = true;
  errorMessage: string | null = null;

  form: FormGroup;
  submitting = false;

  constructor(fb: FormBuilder, private leadsService: LeadsService, private cdr: ChangeDetectorRef) {
    this.form = fb.group({
      name: ['', Validators.required],
      email: ['', [Validators.required, Validators.email]],
      title: ['']
    });
  }

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading = true;
    this.errorMessage = null;
    this.leadsService.list().subscribe({
      next: leads => { this.leads = leads; this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.errorMessage = 'Failed to load leads.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    this.leadsService.create(this.form.getRawValue()).subscribe({
      next: () => {
        this.submitting = false;
        this.form.reset();
        this.refresh();
      },
      error: () => {
        this.submitting = false;
        this.errorMessage = 'Failed to create lead.';
        this.cdr.markForCheck();
      }
    });
  }
}
