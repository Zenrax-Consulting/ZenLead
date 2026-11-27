import { ChangeDetectorRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { InboxModule } from '../inbox-module';
import { InboxMessage } from '../inbox.models';
import { InboxService } from '../inbox.service';
import { ReplyComposer } from './reply-composer';

const message = (status: 'Sent' | 'Failed' = 'Sent'): InboxMessage => ({
  id: 'm9', direction: 'Outbound', fromAddress: 'us@x.test', fromName: null, subject: 'Re: Hi', textBody: 'Tuesday?', fullTextBody: null,
  occurredAt: '2026-11-26T10:00:00', classification: 'Unclassified', classificationConfidence: null, classifiedBy: 'None', needsReview: false,
  senderMismatch: false, isAutoReply: false, deliveryStatus: status, deliveryError: status === 'Failed' ? 'boom' : null
});

describe('ReplyComposer', () => {
  let reply: ReturnType<typeof vi.fn>;

  const build = () => {
    reply = vi.fn(() => of(message()));
    const c = new ReplyComposer({ reply } as unknown as InboxService, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    c.threadId = 't1';
    c.subject = 'Hi';
    return c;
  };

  it('cannot send when empty, while sending, or when the lead cannot be replied to', () => {
    const c = build();
    expect(c.canSend).toBe(false);
    c.body.setValue('   ');
    expect(c.canSend).toBe(false);
    c.body.setValue('Tuesday?');
    expect(c.canSend).toBe(true);
    c.sending = true;
    expect(c.canSend).toBe(false);
    c.sending = false;
    c.canReply = false;
    expect(c.canSend).toBe(false);
    c.send();
    expect(reply).not.toHaveBeenCalled();
  });

  it('shows Re: once', () => {
    const c = build();
    expect(c.replySubject).toBe('Re: Hi');
    c.subject = 'RE: Hi';
    expect(c.replySubject).toBe('RE: Hi');
  });

  it('success clears the box, emits the message and uses a fresh id next time', () => {
    const c = build();
    const sent: InboxMessage[] = [];
    c.sent.subscribe(m => sent.push(m));
    c.body.setValue('Tuesday?');
    c.send();
    c.body.setValue('And Wednesday?');
    c.send();

    expect(sent).toHaveLength(2);
    expect(c.body.value).toBe('');
    const ids = reply.mock.calls.map(call => call[1].clientRequestId);
    expect(ids[0]).toBeTruthy();
    expect(ids[0]).not.toBe(ids[1]);
  });

  it('keeps one id while a request is in flight (a double click cannot send twice)', () => {
    const c = build();
    const pending = new Subject<InboxMessage>();
    reply.mockReturnValue(pending);
    c.body.setValue('Tuesday?');
    c.send();
    c.send();
    expect(reply).toHaveBeenCalledTimes(1);
    expect(c.sending).toBe(true);
  });

  it('a 502 keeps the text, shows the error, emits the failed row, and the retry uses a new id', () => {
    const c = build();
    const failed: InboxMessage[] = [];
    c.failed.subscribe(m => failed.push(m));
    reply.mockReturnValueOnce(throwError(() => ({ status: 502, error: { code: 'send_failed', message: 'SendGrid said no', sentMessage: message('Failed') } })));
    c.body.setValue('Tuesday?');
    c.send();

    expect(c.body.value).toBe('Tuesday?');
    expect(c.errorMessage).toContain('SendGrid said no');
    expect(failed[0].deliveryStatus).toBe('Failed');
    c.send();
    const [first, second] = reply.mock.calls.map(call => call[1].clientRequestId);
    expect(second).not.toBe(first);
  });

  it('a network error keeps the same id so retrying the attempt stays idempotent', () => {
    const c = build();
    reply.mockReturnValueOnce(throwError(() => ({ status: 0 })));
    c.body.setValue('Tuesday?');
    c.send();
    c.send();
    const [first, second] = reply.mock.calls.map(call => call[1].clientRequestId);
    expect(second).toBe(first);
  });

  it('a 409 shows the suppression message and tells the conversation', () => {
    const c = build();
    const blocked: string[] = [];
    c.blocked.subscribe(b => blocked.push(b));
    reply.mockReturnValueOnce(throwError(() => ({ status: 409, error: { code: 'suppressed', message: 'This contact has unsubscribed.' } })));
    c.body.setValue('Tuesday?');
    c.send();
    expect(c.errorMessage).toBe('This contact has unsubscribed.');
    expect(blocked).toEqual(['This contact has unsubscribed.']);
  });

  it('renders the reason instead of the composer when the lead cannot be replied to', async () => {
    TestBed.configureTestingModule({
      imports: [InboxModule],
      providers: [provideRouter([]), { provide: InboxService, useValue: { reply: vi.fn() } }]
    });
    const fixture = TestBed.createComponent(ReplyComposer);
    fixture.componentRef.setInput('threadId', 't1');
    fixture.componentRef.setInput('canReply', false);
    fixture.componentRef.setInput('cannotReplyReason', "This contact has unsubscribed or bounced, so you can't email them.");
    fixture.detectChanges();
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('textarea')).toBeNull();
    expect(el.querySelector('button.send')).toBeNull();
    expect(el.querySelector('.cannot-reply')?.textContent).toContain('unsubscribed or bounced');
  });
});
