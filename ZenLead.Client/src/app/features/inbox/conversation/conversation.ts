import { ChangeDetectorRef, Component, EventEmitter, Inject, Input, OnChanges, OnDestroy, Output, SimpleChanges, ViewChild } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { MatMenuTrigger } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import { formatInZone, statusChip } from '../../campaigns/format';
import { StatusChip } from '../../campaigns/format';
import { NEEDS_REVIEW_CHIP, classificationChip } from '../inbox-format';
import { InboxMessage, MANUAL_LABELS, ReplyClassification, ThreadDetail, ThreadListItem } from '../inbox.models';
import { InboxService } from '../inbox.service';
import { InboxUnreadService } from '../inbox-unread.service';

@Component({
  selector: 'app-inbox-unsubscribe-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>Mark as unsubscribe?</h2>
    <mat-dialog-content>This stops all emails to {{ email }} and can't be undone from here.</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="false">Cancel</button>
      <button mat-raised-button color="warn" [mat-dialog-close]="true">Unsubscribe</button>
    </mat-dialog-actions>`
})
export class UnsubscribeConfirmDialog {
  constructor(@Inject(MAT_DIALOG_DATA) public data: { email: string }) {}
  get email(): string { return this.data.email; }
}

/**
 * One conversation. Opening it marks the thread read (server side) and refreshes the unread badge.
 * Message bodies are rendered with text interpolation only — never innerHTML — so a hostile reply stays text.
 */
@Component({
  selector: 'app-conversation',
  standalone: false,
  templateUrl: './conversation.html',
  styleUrl: './conversation.css'
})
export class Conversation implements OnChanges, OnDestroy {
  @Input({ required: true }) threadId!: string;
  @Input() showBack = false;
  @Output() back = new EventEmitter<void>();
  /** The thread was opened (and is now read on the server). */
  @Output() opened = new EventEmitter<string>();
  /** The classification changed: the list row to replace. */
  @Output() reclassified = new EventEmitter<ThreadListItem>();
  /** A reply was recorded: the list should refresh its snippet/time. */
  @Output() replied = new EventEmitter<void>();

  @ViewChild('labelMenu', { read: MatMenuTrigger }) labelMenu?: MatMenuTrigger;

  readonly labels = MANUAL_LABELS;
  readonly chip = classificationChip;
  readonly needsReviewChip = NEEDS_REVIEW_CHIP;
  readonly zone = Intl.DateTimeFormat().resolvedOptions().timeZone;

  detail: ThreadDetail | null = null;
  loading = false;
  errorMessage: string | null = null;
  reclassifying = false;
  readonly showQuoted = new Set<string>();

  private loadSub?: Subscription;

  constructor(
    private inbox: InboxService,
    private unread: InboxUnreadService,
    private dialog: MatDialog,
    private snackBar: MatSnackBar,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['threadId'] && this.threadId) this.load(true);
  }

  ngOnDestroy(): void { this.loadSub?.unsubscribe(); }

  load(opening = false): void {
    this.loadSub?.unsubscribe();
    if (opening) { this.detail = null; this.showQuoted.clear(); }
    this.loading = true;
    this.errorMessage = null;
    const id = this.threadId;
    this.loadSub = this.inbox.thread(id).subscribe({
      next: detail => {
        this.detail = detail;
        this.loading = false;
        if (opening) {
          this.opened.emit(id);
          this.unread.refreshNow();
        }
        this.cdr.markForCheck();
      },
      error: err => {
        this.loading = false;
        this.errorMessage = err?.status === 404 ? 'This conversation was not found.' : 'Failed to load the conversation.';
        this.cdr.markForCheck();
      }
    });
  }

  get leadStatusChip(): StatusChip | null {
    if (!this.detail) return null;
    const s = this.detail.leadStatus;
    return s === 'New' || s === 'Contacted' ? { label: s, tone: 'neutral', icon: s === 'New' ? 'fiber_new' : 'outgoing_mail' } : statusChip(s);
  }

  /** The latest inbound message, which drives the review banner. */
  get latestInbound(): InboxMessage | null {
    const inbound = this.detail?.messages.filter(m => m.direction === 'Inbound') ?? [];
    return inbound.length ? inbound[inbound.length - 1] : null;
  }

  get reviewMessage(): InboxMessage | null {
    const m = this.latestInbound;
    return m?.needsReview ? m : null;
  }

  confidencePercent(m: InboxMessage): string {
    return m.classificationConfidence == null ? '' : `${Math.round(m.classificationConfidence * 100)}%`;
  }

  isOutbound(m: InboxMessage): boolean { return m.direction === 'Outbound'; }

  senderLabel(m: InboxMessage): string {
    return this.isOutbound(m) ? 'You' : (this.detail?.leadName || m.fromName || m.fromAddress);
  }

  when(m: InboxMessage): string { return formatInZone(m.occurredAt, this.zone); }

  toggleQuoted(id: string): void {
    if (this.showQuoted.has(id)) this.showQuoted.delete(id); else this.showQuoted.add(id);
  }

  confirmSuggested(): void {
    const m = this.reviewMessage;
    if (m) this.setLabel(m.classification);
  }

  chooseLabel(): void { this.labelMenu?.openMenu(); }

  setLabel(label: ReplyClassification): void {
    if (!this.detail || this.reclassifying) return;
    if (label === 'Unsubscribe') {
      this.dialog.open(UnsubscribeConfirmDialog, { data: { email: this.detail.leadEmail } }).afterClosed()
        .subscribe(ok => { if (ok) this.applyLabel(label); });
    } else this.applyLabel(label);
  }

  private applyLabel(label: ReplyClassification): void {
    const id = this.threadId;
    this.reclassifying = true;
    this.inbox.reclassify(id, label).subscribe({
      next: item => {
        this.reclassifying = false;
        this.reclassified.emit(item);
        this.load();                                           // lead status, review flag and canReply may all have changed
      },
      error: err => {
        this.reclassifying = false;
        this.snackBar.open(err?.error?.message ?? 'Could not change the classification.', 'OK', { duration: 6000 });
        this.cdr.markForCheck();
      }
    });
  }

  onSent(message: InboxMessage): void {
    this.detail?.messages.push(message);
    this.replied.emit();
    this.unread.refreshNow();
    this.cdr.markForCheck();
  }

  onFailed(message: InboxMessage): void {
    this.detail?.messages.push(message);
    this.cdr.markForCheck();
  }

  onBlocked(): void { this.load(); }
}
