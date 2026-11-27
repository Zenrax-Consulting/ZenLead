import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Subject, of } from 'rxjs';
import { CampaignsService } from '../../campaigns/campaigns.service';
import { mergeById, toThreadQuery } from '../inbox-format';
import { ThreadListItem } from '../inbox.models';
import { InboxService } from '../inbox.service';
import { FakeDocument } from '../fake-document';
import { INBOX_POLL_MS, InboxPage, parseInboxState } from './inbox-page';

const item = (id: string, over: Partial<ThreadListItem> = {}): ThreadListItem => ({
  id, leadId: `l-${id}`, leadName: `Lead ${id}`, leadEmail: `${id}@x.test`, companyName: null, campaignId: null, campaignName: null,
  subject: 'Hi', snippet: 'Hello', lastMessageAt: '2026-11-26T10:00:00', isRead: false, classification: 'Unclassified',
  needsReview: false, messageCount: 2, ...over
});

const page = (items: ThreadListItem[]) => of({ items, total: items.length, page: 1, pageSize: 25 });

describe('InboxPage', () => {
  let threads: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.fn>;
  let queryParams: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  let narrow: BehaviorSubject<{ matches: boolean }>;
  let doc: FakeDocument;
  let route: { queryParamMap: BehaviorSubject<unknown>; firstChild: unknown };
  let events: Subject<unknown>;

  const build = (params: Record<string, string> = {}, selected: string | null = null, items = [item('a'), item('b')]) => {
    vi.useFakeTimers();
    threads = vi.fn(() => page(items));
    navigate = vi.fn();
    queryParams = new BehaviorSubject(convertToParamMap(params));
    narrow = new BehaviorSubject<{ matches: boolean }>({ matches: false });
    doc = new FakeDocument();
    events = new Subject();
    route = {
      queryParamMap: queryParams as BehaviorSubject<unknown>,
      firstChild: selected ? { snapshot: { paramMap: convertToParamMap({ threadId: selected }) } } : null
    };
    const p = new InboxPage(route as unknown as ActivatedRoute, { navigate, events } as unknown as Router,
      { threads } as unknown as InboxService, { list: () => of([]) } as unknown as CampaignsService,
      { observe: () => narrow } as unknown as BreakpointObserver, doc as unknown as Document,
      { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    p.ngOnInit();
    return p;
  };

  afterEach(() => vi.useRealTimers());

  it('maps URL filters to the service query', () => {
    build({ filter: 'needsReview', campaignId: 'c1', q: 'acme', page: '2' });
    expect(threads).toHaveBeenCalledWith({ page: 2, pageSize: 25, needsReview: true, campaignId: 'c1', q: 'acme' });
    expect(toThreadQuery('Interested', 1, 25)).toEqual({ page: 1, pageSize: 25, classification: 'Interested' });
    expect(toThreadQuery('unread', 1, 25)).toEqual({ page: 1, pageSize: 25, unread: true });
    expect(parseInboxState(convertToParamMap({ filter: 'bogus', page: '-3' }))).toEqual({ filter: 'all', page: 1, campaignId: undefined, q: undefined });
  });

  it('changing a filter writes query params and resets paging', () => {
    const p = build({ page: '3' });
    p.setFilter('Interested');
    expect(navigate.mock.calls[0][1].queryParams).toEqual({ filter: 'Interested', page: null });
    p.setFilter('all');
    expect(navigate.mock.calls[1][1].queryParams).toEqual({ filter: null, page: null });
    p.setCampaign('c2');
    expect(navigate.mock.calls[2][1].queryParams).toEqual({ campaignId: 'c2', page: null });
    p.setPage(4);
    expect(navigate.mock.calls[3][1].queryParams).toEqual({ page: 4 });
  });

  it('search is debounced and resets paging', () => {
    const p = build();
    p.searchControl.setValue('ac');
    p.searchControl.setValue('acme ');
    vi.advanceTimersByTime(300);
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate.mock.calls[0][1].queryParams).toEqual({ q: 'acme', page: null });
  });

  it('polling merges by id without losing the selected thread or unchanged rows', () => {
    const p = build({}, 'b');
    const before = p.threads;
    threads.mockReturnValue(page([item('c'), item('a'), item('b', { snippet: 'new reply' })]));

    vi.advanceTimersByTime(INBOX_POLL_MS);

    expect(threads).toHaveBeenCalledTimes(2);
    expect(p.selectedId).toBe('b');
    expect(p.threads.map(t => t.id)).toEqual(['c', 'a', 'b']);
    expect(p.threads[1]).toBe(before[0]);                // unchanged row: same object, no re-render
    expect(p.threads[2].snippet).toBe('new reply');
    expect(mergeById([item('x')], [])).toEqual([]);
  });

  it('polling stops while the tab is hidden and resumes when visible', () => {
    build();
    doc.setVisible(false);
    vi.advanceTimersByTime(INBOX_POLL_MS * 3);
    expect(threads).toHaveBeenCalledTimes(1);
    doc.setVisible(true);
    vi.advanceTimersByTime(INBOX_POLL_MS);
    expect(threads).toHaveBeenCalledTimes(2);
  });

  it('narrow screens show the list or the conversation, with a way back', () => {
    const p = build({}, null);
    narrow.next({ matches: true });
    expect([p.showList, p.showConversation]).toEqual([true, false]);

    const selected = build({}, 'a');
    narrow.next({ matches: true });
    expect([selected.showList, selected.showConversation]).toEqual([false, true]);
    selected.closeConversation();
    expect(navigate).toHaveBeenCalledWith(['/inbox'], { queryParamsHandling: 'preserve' });

    narrow.next({ matches: false });
    expect([selected.showList, selected.showConversation]).toEqual([true, true]);
  });

  it('marks the opened row read and replaces a reclassified row', () => {
    const p = build();
    p.onOpened('a');
    expect(p.threads[0].isRead).toBe(true);
    p.onReclassified(item('b', { classification: 'Interested' }));
    expect(p.threads[1].classification).toBe('Interested');
  });

  it('tells "no conversations yet" apart from "no matches"', () => {
    expect(build({}, null, []).everEmpty).toBe(true);
    expect(build({ filter: 'unread' }, null, []).everEmpty).toBe(false);
  });
});
