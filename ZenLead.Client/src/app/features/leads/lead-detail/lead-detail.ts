import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
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

  constructor(private route: ActivatedRoute, private leadsService: LeadsService, private cdr: ChangeDetectorRef) {}

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
}
