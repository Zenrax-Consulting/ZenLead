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
