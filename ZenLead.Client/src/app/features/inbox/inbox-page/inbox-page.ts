import { BreakpointObserver } from '@angular/cdk/layout';
import { DOCUMENT } from '@angular/common';
import { ChangeDetectorRef, Component, ElementRef, Inject, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, ParamMap, Params, Router } from '@angular/router';
import { EMPTY, Subscription, debounceTime, distinctUntilChanged, filter, switchMap, timer } from 'rxjs';
import { CampaignListItem } from '../../campaigns/campaigns.models';
import { CampaignsService } from '../../campaigns/campaigns.service';
import { FILTERS, InboxFilter, NEEDS_REVIEW_CHIP, classificationChip, isFilter, mergeById, pageVisible$, relativeTime, toThreadQuery } from '../inbox-format';
import { ThreadListItem, ThreadListQuery } from '../inbox.models';
import { InboxService } from '../inbox.service';

export const INBOX_POLL_MS = 30_000;
export const PAGE_SIZE = 25;

export interface InboxState { filter: InboxFilter; page: number; campaignId?: string; q?: string; }

/** URL query params → filter state; unknown values are dropped. */
export function parseInboxState(params: ParamMap): InboxState {
  const f = params.get('filter');
  const page = Number(params.get('page'));
  return {
    filter: isFilter(f) ? f : 'all',
    page: Number.isInteger(page) && page >= 1 ? page : 1,
    campaignId: params.get('campaignId') || undefined,
    q: params.get('q') || undefined
  };
}

/**
 * Master/detail inbox. Selection lives in the URL (/inbox/:threadId), filters in query params. Polls every 30 s while the tab is
 * visible; a poll merges rows by id so the selection and scroll position survive, and shows "N new" when the user is scrolled down.
 */
@Component({
  selector: 'app-inbox-page',
  standalone: false,
  templateUrl: './inbox-page.html',
  styleUrl: './inbox-page.css'
})
export class InboxPage implements OnInit, OnDestroy {
  readonly filters = FILTERS;
  readonly chip = classificationChip;
  readonly needsReviewChip = NEEDS_REVIEW_CHIP;
  readonly pageSize = PAGE_SIZE;

  state: InboxState = { filter: 'all', page: 1 };
  threads: ThreadListItem[] = [];
  total = 0;
  loading = true;
  errorMessage: string | null = null;
  selectedId: string | null = null;
  isNarrow = false;
  newCount = 0;
  campaigns: CampaignListItem[] = [];
  /** True once the workspace is known to have no conversations at all (vs. none matching the filters). */
  everEmpty = false;

  searchControl = new FormControl<string>('', { nonNullable: true });

  @ViewChild('list') listRef?: ElementRef<HTMLElement>;

  private subs = new Subscription();
  private loadSub?: Subscription;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private inbox: InboxService,
    private campaignsService: CampaignsService,
    private breakpoints: BreakpointObserver,
    @Inject(DOCUMENT) private document: Document,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.subs.add(this.route.queryParamMap.subscribe(params => {
      this.state = parseInboxState(params);
      this.searchControl.setValue(this.state.q ?? '', { emitEvent: false });
      this.load();
    }));
    this.syncSelection();
    this.subs.add(this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(() => this.syncSelection()));
    this.subs.add(this.searchControl.valueChanges.pipe(debounceTime(300), distinctUntilChanged())
      .subscribe(q => this.navigate({ q: q.trim() || null })));
    this.subs.add(this.breakpoints.observe('(max-width: 800px)').subscribe(r => { this.isNarrow = r.matches; this.cdr.markForCheck(); }));
    this.subs.add(pageVisible$(this.document).pipe(switchMap(visible => (visible ? timer(INBOX_POLL_MS, INBOX_POLL_MS) : EMPTY)))
      .subscribe(() => this.poll()));
    this.campaignsService.list().subscribe({
      next: list => { this.campaigns = list; this.cdr.markForCheck(); },
      error: () => {}
    });
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.loadSub?.unsubscribe();
  }

  get query(): ThreadListQuery {
    return toThreadQuery(this.state.filter, this.state.page, PAGE_SIZE, this.state.campaignId, this.state.q);
  }

  get hasFilters(): boolean { return this.state.filter !== 'all' || !!this.state.campaignId || !!this.state.q; }
  get showList(): boolean { return !this.isNarrow || !this.selectedId; }
  get showConversation(): boolean { return !this.isNarrow || !!this.selectedId; }
  get activeFilterLabel(): string { return FILTERS.find(f => f.key === this.state.filter)?.label ?? 'All'; }
  get pageCount(): number { return Math.max(1, Math.ceil(this.total / PAGE_SIZE)); }

  load(): void {
    this.loading = true;
    this.errorMessage = null;
    this.newCount = 0;
    this.loadSub?.unsubscribe();
    this.loadSub = this.inbox.threads(this.query).subscribe({
      next: result => {
        this.threads = result.items;
        this.total = result.total;
        this.everEmpty = result.total === 0 && !this.hasFilters;
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: () => { this.loading = false; this.errorMessage = 'Failed to load conversations.'; this.cdr.markForCheck(); }
    });
  }

  /** Background refresh of the current filter/page; never clears the list or the selection. */
  poll(): void {
    if (this.loading) return;
    this.inbox.threads(this.query).subscribe({
      next: result => {
        const known = new Set(this.threads.map(t => t.id));
        const topChanged = result.items.length > 0 && result.items[0].id !== this.threads[0]?.id;
        if (topChanged && this.scrolledDown) this.newCount = result.items.filter(t => !known.has(t.id)).length || 1;
        this.threads = mergeById(this.threads, result.items);
        this.total = result.total;
        if (result.total > 0) this.everEmpty = false;
        this.cdr.markForCheck();
      },
      error: () => {}
    });
  }

  private get scrolledDown(): boolean { return (this.listRef?.nativeElement.scrollTop ?? 0) > 0; }

  scrollToTop(): void {
    this.listRef?.nativeElement.scrollTo({ top: 0 });
    this.newCount = 0;
  }

  private syncSelection(): void {
    const id = this.route.firstChild?.snapshot.paramMap.get('threadId') ?? null;
    if (id !== this.selectedId) { this.selectedId = id; this.cdr.markForCheck(); }
  }

  // ---- URL-synced state (any filter change resets to page 1) ----

  private navigate(changes: Params, keepPage = false): void {
    this.router.navigate([], {
      relativeTo: this.route.firstChild ?? this.route,
      queryParams: keepPage ? changes : { ...changes, page: null },
      queryParamsHandling: 'merge'
    });
  }

  setFilter(f: InboxFilter): void { this.navigate({ filter: f === 'all' ? null : f }); }
  setCampaign(id: string | null): void { this.navigate({ campaignId: id }); }
  setPage(page: number): void { this.navigate({ page: page <= 1 ? null : page }, true); }

  clearFilters(): void {
    this.searchControl.setValue('', { emitEvent: false });
    this.navigate({ filter: null, campaignId: null, q: null });
  }

  closeConversation(): void {
    this.router.navigate(['/inbox'], { queryParamsHandling: 'preserve' });
  }

  // ---- events from the conversation ----

  onOpened(id: string): void {
    this.threads = this.threads.map(t => (t.id === id && !t.isRead ? { ...t, isRead: true } : t));
    this.cdr.markForCheck();
  }

  onReclassified(item: ThreadListItem): void {
    this.threads = this.threads.map(t => (t.id === item.id ? item : t));
    this.cdr.markForCheck();
  }

  onReplied(): void { this.poll(); }

  when(t: ThreadListItem): string { return relativeTime(t.lastMessageAt); }

  rowLabel(t: ThreadListItem): string {
    return `${t.isRead ? '' : 'Unread. '}${t.leadName}: ${t.subject}. ${this.chip(t.classification).label}${t.needsReview ? ', needs review' : ''}`;
  }
}
