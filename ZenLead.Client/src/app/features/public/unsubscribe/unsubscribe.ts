import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { UnsubscribeInfo, UnsubscribeService } from './unsubscribe.service';

export type UnsubscribeState = 'loading' | 'ready' | 'done' | 'already' | 'invalid' | 'error';

/**
 * Public page behind the unsubscribe link in campaign emails. Opening it never changes anything (mail scanners prefetch
 * links); only the button POSTs. No session is needed or touched.
 */
@Component({
  selector: 'app-unsubscribe',
  standalone: false,
  templateUrl: './unsubscribe.html',
  styleUrl: '../../auth/register/register.css'
})
export class Unsubscribe implements OnInit {
  state: UnsubscribeState = 'loading';
  info: UnsubscribeInfo | null = null;
  submitting = false;
  private token: string | null = null;

  constructor(private route: ActivatedRoute, private unsubscribe: UnsubscribeService, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.paramMap.get('token');
    if (!this.token) {
      this.state = 'invalid';
      return;
    }
    this.unsubscribe.info(this.token).subscribe({
      next: info => {
        this.info = info;
        this.set(info.alreadyUnsubscribed ? 'already' : 'ready');
      },
      error: err => this.set(err.status === 404 ? 'invalid' : 'error')
    });
  }

  confirm(): void {
    if (!this.token || this.submitting) return;
    this.submitting = true;
    this.unsubscribe.confirm(this.token).subscribe({
      next: () => { this.submitting = false; this.set('done'); },
      error: err => { this.submitting = false; this.set(err.status === 404 ? 'invalid' : 'error'); }
    });
  }

  private set(state: UnsubscribeState): void {
    this.state = state;
    this.cdr.markForCheck(); // app is zoneless: async state changes must schedule a render
  }
}
