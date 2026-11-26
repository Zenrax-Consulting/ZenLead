import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { Subscription } from 'rxjs';
import { AiBudget, AiUsageService } from '../../ai/ai-usage.service';

/** Shown in the shell when this month's AI budget is at 80% (Warning) or used up (Exceeded). */
@Component({
  selector: 'app-ai-budget-banner',
  standalone: false,
  templateUrl: './ai-budget-banner.html',
  styleUrl: './ai-budget-banner.css'
})
export class AiBudgetBanner implements OnInit, OnDestroy {
  budget: AiBudget | null = null;
  private sub?: Subscription;

  constructor(private aiUsage: AiUsageService, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.sub = this.aiUsage.budget$.subscribe(b => { this.budget = b; this.cdr.markForCheck(); });
    this.aiUsage.refresh();
  }

  ngOnDestroy(): void { this.sub?.unsubscribe(); }

  get message(): string | null {
    switch (this.budget?.state) {
      case 'Warning': return "80% of this month's AI budget used.";
      case 'Exceeded': return `AI drafts are paused until ${AiBudgetBanner.formatDate(this.budget.resetsOn)}.`;
      default: return null;
    }
  }

  /** "2026-12-01" → "1 December 2026", without time-zone drift. */
  static formatDate(isoDate: string): string {
    const [y, m, d] = isoDate.split('-').map(Number);
    if (!y || !m || !d) return isoDate;
    const month = new Date(Date.UTC(y, m - 1, d)).toLocaleString('en-GB', { month: 'long', timeZone: 'UTC' });
    return `${d} ${month} ${y}`;
  }
}
