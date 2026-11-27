import { ChangeDetectorRef, Component, EventEmitter, Input, Output } from '@angular/core';
import { FormControl } from '@angular/forms';
import { InboxMessage } from '../inbox.models';
import { InboxService } from '../inbox.service';

export const MAX_REPLY_LENGTH = 10_000;

/** Generates a request id; `crypto.randomUUID` needs a secure context, so fall back for plain-http dev hosts. */
export function newRequestId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) return crypto.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, ch => {
    const r = (Math.random() * 16) | 0;
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/**
 * Reply box. One `clientRequestId` per send attempt, kept until the request settles: a network-level retry of the same
 * attempt is idempotent on the server, while a new attempt after a definite failure (502/409/400) gets a new id.
 */
@Component({
  selector: 'app-reply-composer',
  standalone: false,
  templateUrl: './reply-composer.html',
  styleUrl: './reply-composer.css'
})
export class ReplyComposer {
  @Input({ required: true }) threadId!: string;
  @Input() subject = '';
  @Input() canReply = true;
  @Input() cannotReplyReason: string | null = null;
  /** The recorded message: Sent on success, Failed after a 502. */
  @Output() sent = new EventEmitter<InboxMessage>();
  @Output() failed = new EventEmitter<InboxMessage>();
  /** The server said the lead can't be emailed (409): the conversation should reload. */
  @Output() blocked = new EventEmitter<string>();

  readonly maxLength = MAX_REPLY_LENGTH;
  body = new FormControl<string>('', { nonNullable: true });
  sending = false;
  errorMessage: string | null = null;
  /** The id of the attempt in flight (or of an attempt whose outcome is unknown). */
  pendingRequestId: string | null = null;

  constructor(private inbox: InboxService, private cdr: ChangeDetectorRef) {}

  get replySubject(): string {
    const s = this.subject.trim();
    return /^re:/i.test(s) ? s : `Re: ${s}`;
  }

  get canSend(): boolean {
    const text = this.body.value.trim();
    return this.canReply && !this.sending && text.length > 0 && text.length <= MAX_REPLY_LENGTH;
  }

  send(): void {
    if (!this.canSend) return;
    this.pendingRequestId ??= newRequestId();
    this.sending = true;
    this.errorMessage = null;
    this.inbox.reply(this.threadId, { clientRequestId: this.pendingRequestId, body: this.body.value }).subscribe({
      next: message => {
        this.sending = false;
        this.pendingRequestId = null;
        this.body.setValue('');
        this.sent.emit(message);
        this.cdr.markForCheck();
      },
      error: err => {
        this.sending = false;
        const status: number = err?.status ?? 0;
        if (status === 502) {
          this.pendingRequestId = null;                       // definite failure: the retry is a new attempt
          this.errorMessage = `The email could not be sent${err.error?.message ? `: ${err.error.message}` : '.'} Your text is kept — try again.`;
          if (err.error?.sentMessage) this.failed.emit(err.error.sentMessage);
        } else if (status === 409) {
          this.pendingRequestId = null;
          this.errorMessage = err.error?.message ?? "This contact has unsubscribed or bounced, so you can't email them.";
          this.blocked.emit(this.errorMessage!);
        } else if (status === 400) {
          this.pendingRequestId = null;
          this.errorMessage = 'Check the reply text and try again.';
        } else if (status === 429) {
          this.pendingRequestId = null;
          this.errorMessage = 'Too many replies in a minute. Wait a moment and try again.';
        } else {
          // unknown outcome (network/5xx): keep the id so trying again cannot send twice
          this.errorMessage = 'Could not reach the server. Your text is kept — try again.';
        }
        this.cdr.markForCheck();
      }
    });
  }

  onKeydown(e: KeyboardEvent): void {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); this.send(); }
  }
}
