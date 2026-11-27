import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { InboxModule } from '../inbox-module';
import { InboxMessage, ThreadDetail } from '../inbox.models';
import { InboxService } from '../inbox.service';
import { InboxUnreadService } from '../inbox-unread.service';
import { Conversation, UnsubscribeConfirmDialog } from './conversation';

const msg = (over: Partial<InboxMessage>): InboxMessage => ({
  id: 'm1', direction: 'Inbound', fromAddress: 'jane@acme.test', fromName: 'Jane', subject: 'Re: Hi', textBody: 'Hello', fullTextBody: null,
  occurredAt: '2026-11-26T10:00:00', classification: 'Unclassified', classificationConfidence: null, classifiedBy: 'None', needsReview: false,
  senderMismatch: false, isAutoReply: false, deliveryStatus: null, deliveryError: null, ...over
});

const detail = (messages: InboxMessage[], over: Partial<ThreadDetail> = {}): ThreadDetail => ({
  id: 't1', leadId: 'l1', leadName: 'Jane Doe', leadEmail: 'jane@acme.test', leadTitle: 'CTO', companyName: 'Acme', leadStatus: 'Replied',
  campaignId: 'c1', campaignName: 'Q4', subject: 'Hi', classification: 'Unclassified', canReply: true, cannotReplyReason: null, messages, ...over
});

describe('Conversation', () => {
  let service: { thread: ReturnType<typeof vi.fn>; reclassify: ReturnType<typeof vi.fn>; reply: ReturnType<typeof vi.fn> };
  let unread: { refreshNow: ReturnType<typeof vi.fn> };
  let dialogResult: boolean;
  let dialog: { open: ReturnType<typeof vi.fn> };

  const render = async (d: ThreadDetail) => {
    service = {
      thread: vi.fn(() => of(d)),
      reclassify: vi.fn(() => of({ id: 't1', classification: 'Interested' })),
      reply: vi.fn()
    };
    unread = { refreshNow: vi.fn() };
    dialogResult = true;
    dialog = { open: vi.fn(() => ({ afterClosed: () => of(dialogResult) })) };
    TestBed.configureTestingModule({
      imports: [InboxModule],
      providers: [
        provideRouter([]),
        { provide: InboxService, useValue: service },
        { provide: InboxUnreadService, useValue: unread },
        { provide: MatDialog, useValue: dialog }
      ]
    });
    const fixture = TestBed.createComponent(Conversation);
    fixture.componentRef.setInput('threadId', 't1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, cmp: fixture.componentInstance };
  };

  it('renders hostile message bodies as literal text', async () => {
    const hostile = '<img src=x onerror=alert(1)><script>alert(2)</script><b>bold</b>';
    const { el } = await render(detail([msg({ textBody: hostile })]));
    const body = el.querySelector('.message .body')!;
    expect(body.textContent).toContain(hostile);
    expect(el.querySelector('.messages img')).toBeNull();
    expect(el.querySelector('.messages script')).toBeNull();
    expect(el.querySelector('.messages b')).toBeNull();
  });

  it('opening a thread loads it (marking it read) and refreshes the unread badge', async () => {
    const opened: string[] = [];
    const { cmp } = await render(detail([msg({})]));
    cmp.opened.subscribe(id => opened.push(id));
    cmp.load(true);
    expect(service.thread).toHaveBeenCalledWith('t1');
    expect(unread.refreshNow).toHaveBeenCalled();
    expect(opened).toEqual(['t1']);
  });

  it('toggles quoted text', async () => {
    const { fixture, el } = await render(detail([msg({ textBody: 'Yes please', fullTextBody: 'Yes please\n\n> On Monday you wrote...' })]));
    expect(el.querySelector('.body')!.textContent).toBe('Yes please');
    (el.querySelector('.quoted-toggle') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('.body')!.textContent).toContain('On Monday you wrote');
    expect(el.querySelector('.quoted-toggle')!.textContent).toContain('Hide quoted text');
  });

  it('shows auto-reply, sender-mismatch and failed-delivery chips, and labels outbound as You', async () => {
    const { el } = await render(detail([
      msg({ id: 'a', isAutoReply: true, senderMismatch: true }),
      msg({ id: 'b', direction: 'Outbound', deliveryStatus: 'Failed', deliveryError: '550 nope' })
    ]));
    expect(el.querySelector('.auto-reply')).not.toBeNull();
    expect(el.querySelector('.mismatch')!.textContent).toContain('different address');
    expect(el.querySelector('.delivery')!.textContent).toContain('550 nope');
    const senders = Array.from(el.querySelectorAll('.sender')).map(s => s.textContent);
    expect(senders).toEqual(['Jane Doe', 'You']);
  });

  it('the needs-review banner shows the suggestion and Confirm reclassifies with it', async () => {
    const { el } = await render(detail([msg({ classification: 'Interested', classificationConfidence: 0.62, needsReview: true })]));
    const banner = el.querySelector('.review-banner')!;
    expect(banner.textContent).toContain('Interested');
    expect(banner.textContent).toContain('62%');
    (banner.querySelector('button.confirm') as HTMLButtonElement).click();
    expect(service.reclassify).toHaveBeenCalledWith('t1', 'Interested');
  });

  it('choosing Unsubscribe asks for confirmation first', async () => {
    const { cmp } = await render(detail([msg({})]));
    dialogResult = false;
    cmp.setLabel('Unsubscribe');
    expect(dialog.open).toHaveBeenCalledWith(UnsubscribeConfirmDialog, { data: { email: 'jane@acme.test' } });
    expect(service.reclassify).not.toHaveBeenCalled();

    dialogResult = true;
    cmp.setLabel('Unsubscribe');
    expect(service.reclassify).toHaveBeenCalledWith('t1', 'Unsubscribe');
  });

  it('shows the reason instead of the composer when the lead cannot be emailed', async () => {
    const { el } = await render(detail([msg({})], { canReply: false, cannotReplyReason: 'This contact has unsubscribed.' }));
    expect(el.querySelector('textarea')).toBeNull();
    expect(el.querySelector('.cannot-reply')!.textContent).toContain('This contact has unsubscribed.');
  });
});
