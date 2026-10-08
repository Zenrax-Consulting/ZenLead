import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { FormControl } from '@angular/forms';
import { ActivatedRoute, Params, Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { PageEvent } from '@angular/material/paginator';
import { Sort } from '@angular/material/sort';
import { Subscription, debounceTime, distinctUntilChanged } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { AddLeadDialog } from '../add-lead-dialog/add-lead-dialog';
import { CompaniesService } from '../companies.service';
import { LeadSelectionService } from '../lead-selection.service';
import {
  CompanySummary, LEAD_SOURCES, LEAD_STATUSES, Lead, LeadQuery, LeadSource, LeadStatus, PagedResult
} from '../leads.models';
import { LeadsService } from '../leads.service';
import { ProfileDraftService } from '../profiles/profile-draft.service';
import { ProfilesService } from '../profiles/profiles.service';

const DEFAULT_SORT = '-createdAt';
const SORTABLE = ['name', 'email', 'company', 'status', 'createdAt'];
const PAGE_SIZES = [25, 50, 100];

/** URL query params → typed LeadQuery. Unknown or malformed values are dropped rather than sent to the API. */
export function parseLeadQuery(params: Params): LeadQuery {
  const page = Number(params['page']);
  const pageSize = Number(params['pageSize']);
  const sort = typeof params['sort'] === 'string' ? params['sort'] : undefined;
  const status = params['status'] as LeadStatus;
  const source = params['source'] as LeadSource;
  return {
    page: Number.isInteger(page) && page >= 1 ? page : 1,
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : 25,
    q: params['q'] || undefined,
    status: LEAD_STATUSES.includes(status) ? status : undefined,
    source: LEAD_SOURCES.includes(source) ? source : undefined,
    companyId: params['companyId'] || undefined,
    sourceRunId: params['sourceRunId'] || undefined,
    sort: sort && SORTABLE.includes(sort.replace(/^-/, '')) ? sort : undefined
  };
}

@Component({
  selector: 'app-leads-list',
  standalone: false,
  templateUrl: './leads-list.html',
  styleUrl: './leads-list.css'
})
export class LeadsList implements OnInit, OnDestroy {
  readonly columns = ['select', 'name', 'email', 'company', 'title', 'source', 'status', 'createdAt'];
  readonly statuses = LEAD_STATUSES;
  readonly sources = LEAD_SOURCES;
  readonly features = environment.features;

  query: LeadQuery = { page: 1, pageSize: 25 };
  result: PagedResult<Lead> | null = null;
  loading = true;
  errorMessage: string | null = null;

  searchControl = new FormControl<string>('', { nonNullable: true });
  companyControl = new FormControl<string | CompanySummary>('', { nonNullable: true });
  companyOptions: CompanySummary[] = [];
  activeCompany: CompanySummary | null = null;

  private subs = new Subscription();
  private loadSub?: Subscription;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private leadsService: LeadsService,
    private companies: CompaniesService,
    public selection: LeadSelectionService,
    private dialog: MatDialog,
    private cdr: ChangeDetectorRef,
    private profiles: ProfilesService,
    private drafts: ProfileDraftService,
    private snackBar: MatSnackBar
  ) {}

  ngOnInit(): void {
    this.subs.add(this.route.queryParams.subscribe(params => {
      this.query = parseLeadQuery(params);
      this.searchControl.setValue(this.query.q ?? '', { emitEvent: false });
      this.syncActiveCompany();
      this.load();
    }));

    this.subs.add(this.searchControl.valueChanges.pipe(debounceTime(300), distinctUntilChanged())
      .subscribe(q => this.navigate({ q: q.trim() || null })));

    this.subs.add(this.companyControl.valueChanges.pipe(debounceTime(300)).subscribe(value => {
      if (typeof value !== 'string') return;
      this.companies.search(value).subscribe({
        next: options => { this.companyOptions = options; this.cdr.markForCheck(); },
        error: () => {}
      });
    }));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.loadSub?.unsubscribe();
  }

  load(): void {
    this.loading = true;
    this.errorMessage = null;
    this.loadSub?.unsubscribe();
    const query = this.query;
    this.loadSub = this.leadsService.list(query).subscribe({
      next: result => {
        this.result = result;
        this.loading = false;
        this.selection.lastQuery = query;
        this.selection.lastTotal = result.total;
        this.cdr.markForCheck();
      },
      error: () => { this.errorMessage = 'Failed to load leads.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  get leads(): Lead[] { return this.result?.items ?? []; }
  get total(): number { return this.result?.total ?? 0; }
  get hasFilters(): boolean {
    const q = this.query;
    return !!(q.q || q.status || q.source || q.companyId || q.sourceRunId);
  }

  // ---- URL-synced state changes (any filter/sort change resets to page 1) ----

  private navigate(changes: Params, keepPage = false): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: keepPage ? changes : { ...changes, page: null },
      queryParamsHandling: 'merge'
    });
  }

  onStatus(status: LeadStatus | null): void { this.navigate({ status }); }
  onSource(source: LeadSource | null): void { this.navigate({ source }); }

  onPage(e: PageEvent): void {
    const sizeChanged = e.pageSize !== this.query.pageSize;
    this.navigate(
      { page: sizeChanged || e.pageIndex === 0 ? null : e.pageIndex + 1, pageSize: e.pageSize === 25 ? null : e.pageSize },
      true);
  }

  onSort(e: Sort): void {
    this.navigate({ sort: e.direction ? (e.direction === 'desc' ? '-' : '') + e.active : null });
  }

  get sortActive(): string { return (this.query.sort ?? DEFAULT_SORT).replace(/^-/, ''); }
  get sortDirection(): 'asc' | 'desc' { return (this.query.sort ?? DEFAULT_SORT).startsWith('-') ? 'desc' : 'asc'; }

  companyLabel = (value: string | CompanySummary | null): string => (value && typeof value !== 'string' ? value.name : '');

  onCompanySelected(company: CompanySummary): void {
    this.activeCompany = company;
    this.navigate({ companyId: company.id });
  }

  clearCompany(): void {
    this.activeCompany = null;
    this.companyControl.setValue('', { emitEvent: false });
    this.navigate({ companyId: null });
  }

  clearSourceRun(): void { this.navigate({ sourceRunId: null }); }

  clearFilters(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: null, status: null, source: null, companyId: null, sourceRunId: null, page: null },
      queryParamsHandling: 'merge'
    });
  }

  private syncActiveCompany(): void {
    const id = this.query.companyId;
    if (!id) { this.activeCompany = null; return; }
    if (this.activeCompany?.id === id) return;
    this.companies.getById(id).subscribe({
      next: c => { this.activeCompany = c; this.cdr.markForCheck(); },
      error: () => {}
    });
  }

  // ---- selection (persists across pages in LeadSelectionService) ----

  get allOnPageSelected(): boolean { return this.leads.length > 0 && this.leads.every(l => this.selection.isSelected(l.id)); }
  get someOnPageSelected(): boolean { return !this.allOnPageSelected && this.leads.some(l => this.selection.isSelected(l.id)); }

  toggleAllOnPage(checked: boolean): void { this.selection.setMany(this.leads.map(l => l.id), checked); }

  // ---- target profile from selection (F14) ----

  onlyReplied = false;
  creatingProfile = false;

  /** Statuses are only known for the current page, so the option appears when a selected lead on this page has replied. */
  get repliedSelected(): boolean { return this.leads.some(l => l.status === 'Replied' && this.selection.isSelected(l.id)); }

  createProfileFromSelection(): void {
    if (this.creatingProfile || this.selection.count === 0) return;
    this.creatingProfile = true;
    this.profiles.suggestFromLeads(this.selection.selected, this.onlyReplied && this.repliedSelected).subscribe({
      next: draft => {
        this.creatingProfile = false;
        this.drafts.set(draft);
        this.router.navigate(['/leads/profiles/new']);
      },
      error: err => {
        this.creatingProfile = false;
        this.snackBar.open(err.status === 400 ? (err.error?.message ?? 'Could not build a profile from those leads.') : 'Failed to create a target profile.', 'OK', { duration: 6000 });
        this.cdr.markForCheck();
      }
    });
  }

  // ---- add lead ----

  openAddLead(): void {
    this.dialog.open(AddLeadDialog, { width: '480px' }).afterClosed().subscribe(created => {
      if (created) this.load();
    });
  }
}
