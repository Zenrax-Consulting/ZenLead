import { DOCUMENT } from '@angular/common';
import { Inject, Injectable } from '@angular/core';
import { EMPTY, Observable, Subject, catchError, map, merge, shareReplay, switchMap, timer } from 'rxjs';
import { pageVisible$ } from './inbox-format';
import { InboxService } from './inbox.service';

export const UNREAD_POLL_MS = 60_000;

/**
 * The shell's unread badge. Polls every 60 s while the tab is visible (paused when hidden, fetched again on return);
 * `refreshNow()` after reading or replying. A failed poll is skipped, never fatal to the stream.
 */
@Injectable({ providedIn: 'root' })
export class InboxUnreadService {
  private readonly refresh$ = new Subject<void>();
  readonly count$: Observable<number>;

  constructor(private inbox: InboxService, @Inject(DOCUMENT) doc: Document) {
    const ticks = pageVisible$(doc).pipe(switchMap(visible => (visible ? timer(0, UNREAD_POLL_MS) : EMPTY)));
    this.count$ = merge(ticks, this.refresh$).pipe(
      switchMap(() => this.inbox.unreadCount().pipe(map(r => r.count), catchError(() => EMPTY))),
      shareReplay({ bufferSize: 1, refCount: true }));
  }

  refreshNow(): void { this.refresh$.next(); }
}
