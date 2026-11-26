import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApprovalInfo } from '../../../core/auth/auth.models';
import { ApprovalsService } from '../../../core/auth/approvals.service';

export type ApproveState = 'loading' | 'ready' | 'approved' | 'rejected' | 'used' | 'expired' | 'invalid' | 'error';

/**
 * Public page behind the link in the approval email. Opening it never changes anything (mail scanners prefetch links);
 * only the Approve / Reject buttons POST. The token is removed from the address bar as soon as it is read.
 */
@Component({
  selector: 'app-approve',
  standalone: false,
  templateUrl: './approve.html',
  styleUrl: '../register/register.css'
})
export class Approve implements OnInit {
  state: ApproveState = 'loading';
  info: ApprovalInfo | null = null;
  deciding = false;
  private token: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private approvals: ApprovalsService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token');
    this.router.navigate([], { relativeTo: this.route, replaceUrl: true, queryParams: {} });   // out of the address bar and history
    if (!this.token) {
      this.state = 'invalid';
      return;
    }

    this.approvals.info(this.token).subscribe({
      next: info => {
        this.info = info;
        this.state = info.status === 'pending' ? 'ready' : info.status === 'used' ? 'used' : 'expired';
        this.cdr.markForCheck();
      },
      error: err => {
        this.state = err.status === 404 ? 'invalid' : 'error';
        this.cdr.markForCheck();
      }
    });
  }

  decide(decision: 'approve' | 'reject'): void {
    if (!this.token || this.deciding) return;
    this.deciding = true;
    this.approvals.decide(this.token, decision).subscribe({
      next: () => this.finish(decision === 'approve' ? 'approved' : 'rejected'),
      error: err => this.finish(Approve.stateForError(err.status))
    });
  }

  static stateForError(status: number | undefined): ApproveState {
    switch (status) {
      case 409: return 'used';
      case 410: return 'expired';
      case 404: return 'invalid';
      default: return 'error';
    }
  }

  private finish(state: ApproveState): void {
    this.deciding = false;
    this.state = state;
    this.cdr.markForCheck(); // app is zoneless: async state changes must schedule a render
  }
}
