import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable } from 'rxjs';

export type BudgetState = 'Ok' | 'Warning' | 'Exceeded';

export interface AiBudget {
  spentUsd: number;
  capUsd: number;
  state: BudgetState;
  resetsOn: string;          // yyyy-MM-dd (first day of the next UTC month)
}

export interface AiTokenUsage {
  calls: number;
  totalTokens: number;
  estimatedCostUsd: number;
  budget: AiBudget;
}

/** The workspace's AI usage and monthly budget (F23.3). The shell banner listens; callers refresh after each compose. */
@Injectable({ providedIn: 'root' })
export class AiUsageService {
  private readonly budgetSubject = new BehaviorSubject<AiBudget | null>(null);
  readonly budget$: Observable<AiBudget | null> = this.budgetSubject.asObservable();

  constructor(private http: HttpClient) {}

  usage(): Observable<AiTokenUsage> {
    return this.http.get<AiTokenUsage>('/api/v1/ai/token-usage');
  }

  /** Re-reads the budget; a failure keeps the last known value (the banner is a hint, never a blocker). */
  refresh(): void {
    this.usage().subscribe({ next: u => this.budgetSubject.next(u.budget), error: () => {} });
  }
}
