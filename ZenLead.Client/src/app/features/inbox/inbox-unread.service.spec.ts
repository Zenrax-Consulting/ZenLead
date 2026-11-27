import { of, throwError } from 'rxjs';
import { InboxUnreadService, UNREAD_POLL_MS } from './inbox-unread.service';
import { InboxService } from './inbox.service';
import { FakeDocument } from './fake-document';

describe('InboxUnreadService', () => {
  let doc: FakeDocument;
  let unreadCount: ReturnType<typeof vi.fn>;
  let service: InboxUnreadService;

  beforeEach(() => {
    vi.useFakeTimers();
    doc = new FakeDocument();
    unreadCount = vi.fn(() => of({ count: 2 }));
    service = new InboxUnreadService({ unreadCount } as unknown as InboxService, doc as unknown as Document);
  });

  afterEach(() => vi.useRealTimers());

  it('fetches immediately and then every 60 s', () => {
    const counts: number[] = [];
    const sub = service.count$.subscribe(c => counts.push(c));
    vi.advanceTimersByTime(0);
    expect(unreadCount).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(UNREAD_POLL_MS);
    expect(unreadCount).toHaveBeenCalledTimes(2);
    expect(counts).toEqual([2, 2]);
    sub.unsubscribe();
  });

  it('pauses while the tab is hidden and fetches again when it is visible', () => {
    const sub = service.count$.subscribe();
    vi.advanceTimersByTime(0);
    doc.setVisible(false);
    vi.advanceTimersByTime(UNREAD_POLL_MS * 3);
    expect(unreadCount).toHaveBeenCalledTimes(1);
    doc.setVisible(true);
    vi.advanceTimersByTime(0);
    expect(unreadCount).toHaveBeenCalledTimes(2);
    sub.unsubscribe();
  });

  it('refreshNow forces a fetch, and a failed poll does not end the stream', () => {
    const counts: number[] = [];
    const sub = service.count$.subscribe(c => counts.push(c));
    vi.advanceTimersByTime(0);
    unreadCount.mockReturnValueOnce(throwError(() => new Error('offline')));
    service.refreshNow();
    unreadCount.mockReturnValueOnce(of({ count: 0 }));
    service.refreshNow();
    expect(unreadCount).toHaveBeenCalledTimes(3);
    expect(counts).toEqual([2, 0]);
    sub.unsubscribe();
  });
});
