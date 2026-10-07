# Feature 13 — Leads UI: List & Detail

**Branch:** `feature/angular-leads-list`
**Sprint:** 1
**Depends on:** F11 (new lead fields, `PUT`/`DELETE`), F12 (shell + `LeadsModule`).

## Goal
A lead list that survives 50k rows (server-side paging, sort, search, filters, URL-synced), and a lead detail page that shows company, provenance and verification, supports edit/delete, and exposes a **selection service** other features read (target profiles in F14, campaign enrollment in F24).

## Files to add/modify

### Backend

**`ZenLead.Application/Dtos/Leads/LeadQuery.cs`** (new)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record LeadQuery(
    int Page = 1, int PageSize = 25, string? Q = null,
    LeadStatus? Status = null, Guid? CompanyId = null, LeadSource? Source = null, Guid? SourceRunId = null,
    string? Sort = null);   // "name" | "email" | "status" | "company" | "createdAt"; prefix "-" = descending; default "-createdAt"

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
```
Bounds are enforced in the validator, not trusted: `Page >= 1`, `PageSize` 1–100.

**`ZenLead.Application/Validation/Leads/LeadQueryValidator.cs`** (new) — page/pageSize bounds, `Q` ≤ 100 chars, `Sort` in the allow-list (with optional `-`), enums `IsInEnum`.

**`ZenLead.Application/Abstractions/ILeadRepository.cs`** (modified) — add `Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery query, CancellationToken ct = default);` and `Task<IReadOnlyList<Guid>> ListIdsAsync(Guid workspaceId, LeadQuery query, int max, CancellationToken ct = default)` (the "all matching filter" case F24 needs; `max` guards against a runaway select). Add both to `FakeLeadRepository` / `FakeLeadRepositoryForAi`.

**`ZenLead.Infrastructure/Persistence/LeadRepository.cs`** (modified)
```csharp
public async Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery q, CancellationToken ct = default)
{
    var query = ApplyFilters(db.Leads.AsNoTracking().Include(l => l.Company).Where(l => l.WorkspaceId == workspaceId), q);
    var total = await query.CountAsync(ct);
    var items = await ApplySort(query, q.Sort).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
    return new PagedResult<Lead>(items, total, q.Page, q.PageSize);
}

private static IQueryable<Lead> ApplyFilters(IQueryable<Lead> query, LeadQuery q)
{
    if (q.Status is { } status) query = query.Where(l => l.Status == status);
    if (q.CompanyId is { } companyId) query = query.Where(l => l.CompanyId == companyId);
    if (q.Source is { } source) query = query.Where(l => l.Source == source);
    if (q.SourceRunId is { } runId) query = query.Where(l => l.SourceRunId == runId);
    if (!string.IsNullOrWhiteSpace(q.Q))
    {
        var like = $"%{EscapeLike(q.Q.Trim())}%";
        query = query.Where(l => EF.Functions.Like(l.Name, like, "\\") || EF.Functions.Like(l.Email, like, "\\")
                              || (l.Company != null && EF.Functions.Like(l.Company.Name, like, "\\")));
    }
    return query;
}

private static IQueryable<Lead> ApplySort(IQueryable<Lead> query, string? sort)
{
    var desc = sort?.StartsWith('-') ?? true;               // default newest first
    return sort?.TrimStart('-') switch
    {
        "name" => desc ? query.OrderByDescending(l => l.Name).ThenBy(l => l.Id) : query.OrderBy(l => l.Name).ThenBy(l => l.Id),
        "email" => desc ? query.OrderByDescending(l => l.Email).ThenBy(l => l.Id) : query.OrderBy(l => l.Email).ThenBy(l => l.Id),
        "status" => desc ? query.OrderByDescending(l => l.Status).ThenBy(l => l.Id) : query.OrderBy(l => l.Status).ThenBy(l => l.Id),
        "company" => desc ? query.OrderByDescending(l => l.Company!.Name).ThenBy(l => l.Id) : query.OrderBy(l => l.Company!.Name).ThenBy(l => l.Id),
        _ => desc ? query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id) : query.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
    };
}

private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
```
`ThenBy(Id)` makes paging deterministic. `ListIdsAsync` reuses `ApplyFilters` and `.Select(l => l.Id).Take(max)`.

**`ZenLead.Infrastructure/Persistence/Configurations/EntityConfigurations.cs`** — in `LeadConfiguration` add indexes `(WorkspaceId, CreatedAt)`, `(WorkspaceId, Status)`, `(WorkspaceId, CompanyId)`. (`(WorkspaceId, SourceRunId)` and the unique email index came with F11.) Migration **`AddLeadListIndexes`**. Name search is `LIKE '%x%'` (not seekable); at 50k rows per workspace a scan is fine — F29.5 re-measures and moves to a full-text index only if it is not.

**`ZenLead.Api/Controllers/V1/LeadsController.cs`** (modified) — replace `List`:
```csharp
[HttpGet]
public async Task<ActionResult<PagedResult<LeadResponse>>> List([FromQuery] LeadQuery query, CancellationToken ct)
{
    if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
    var validation = await queryValidator.ValidateAsync(query, ct);
    if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

    var page = await leads.SearchAsync(workspaceId, query, ct);
    return Ok(new PagedResult<LeadResponse>(page.Items.Select(ToResponse).ToList(), page.Total, page.Page, page.PageSize));
}
```
Inject `IValidator<LeadQuery>`; update `LeadsControllerTests.BuildController`. **Breaking change:** the list response is now an object, not an array — the Angular service changes in the same PR. Remove `ListByWorkspaceAsync` from the interface and fakes once nothing uses it.

**`ZenLead.Api/Controllers/V1/CompaniesController.cs`** (new) — `GET /api/v1/companies?q=` for the filter dropdown: `[Authorize]`, workspace check, `ICompanyRepository.SearchAsync(workspaceId, q, 20)` → `CompanySummary[]` (new tiny repository + registration). Also returns `GET /api/v1/companies/{id}` if the filter chip needs to resolve a name from a URL `companyId` (needed when the page is opened with `?companyId=`).

### Angular

**`features/leads/leads.models.ts`** (modified)
```ts
export type LeadStatus = 'New' | 'Contacted' | 'Replied' | 'Unsubscribed' | 'Bounced';
export type LeadSource = 'Manual' | 'Csv' | 'Discovery';
export type EmailVerificationStatus = 'Unverified' | 'Verified' | 'Risky' | 'Invalid';

export interface CompanySummary { id: string; name: string; domain: string | null; industry: string | null; country: string | null; }

export interface Lead {
  id: string; name: string; email: string; title: string | null; status: LeadStatus; createdAt: string;
  company: CompanySummary | null; source: LeadSource; sourceRunId: string | null; emailVerificationStatus: EmailVerificationStatus;
}

export interface LeadQuery {
  page: number; pageSize: number; q?: string; status?: LeadStatus; companyId?: string;
  source?: LeadSource; sourceRunId?: string; sort?: string;
}
export interface PagedResult<T> { items: T[]; total: number; page: number; pageSize: number; }
export interface UpdateLeadRequest { name: string; title: string | null; status: LeadStatus; companyName: string | null; companyDomain: string | null; }
```
(JSON enums: the API serialises enums as strings only if `JsonStringEnumConverter` is configured. Phase 1's `LeadStatus` already arrives as a string in `leads.models.ts`, so it is — **verify** it is global in `Program.cs` `AddControllers().AddJsonOptions(...)`; add it there if it was set per-type.)

**`features/leads/leads.service.ts`** (modified)
```ts
list(query: LeadQuery): Observable<PagedResult<Lead>> {
  let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
  for (const key of ['q', 'status', 'companyId', 'source', 'sourceRunId', 'sort'] as const) {
    const value = query[key];
    if (value) params = params.set(key, value);
  }
  return this.http.get<PagedResult<Lead>>('/api/v1/leads', { params });
}
update(id: string, request: UpdateLeadRequest): Observable<Lead> { return this.http.put<Lead>(`/api/v1/leads/${id}`, request); }
delete(id: string): Observable<void> { return this.http.delete<void>(`/api/v1/leads/${id}`); }
```
Add `companies.service.ts` (`search(q)`).

**`features/leads/lead-selection.service.ts`** (new) — root-provided; the contract F14 and F24 use.
```ts
@Injectable({ providedIn: 'root' })
export class LeadSelectionService {
  private ids = new Set<string>();
  /** The filter the list was showing — lets F24 offer "all N matching this filter". */
  lastQuery: LeadQuery | null = null;
  lastTotal = 0;

  get count(): number { return this.ids.size; }
  get selected(): string[] { return [...this.ids]; }
  isSelected(id: string): boolean { return this.ids.has(id); }
  toggle(id: string): void { this.ids.has(id) ? this.ids.delete(id) : this.ids.add(id); }
  setMany(ids: string[], selected: boolean): void { ids.forEach(id => selected ? this.ids.add(id) : this.ids.delete(id)); }
  clear(): void { this.ids.clear(); }
}
```
Selection persists across pages and clears on logout (`AuthService.logout()` → inject and call `clear()` via the shell).

**`features/leads/leads-list/leads-list.ts` / `.html` / `.css`** (rewritten)
- State lives in the **URL query params** (`?page=2&q=acme&status=New&sort=-createdAt`): `ActivatedRoute.queryParams` → `LeadQuery` → `LeadsService.list`. Filter/sort/page changes call `router.navigate([], { queryParams, queryParamsHandling: 'merge' })`, so refresh/back/forward and links from F14 (`?sourceRunId=…`) work for free.
- `mat-table` + `MatPaginator` (`pageSizeOptions=[25,50,100]`, `length=total`) + `MatSort` (columns `name`, `email`, `company`, `status`, `createdAt`), search box debounced 300 ms (`valueChanges.pipe(debounceTime(300), distinctUntilChanged())`), status `mat-select`, source `mat-select`, company typeahead (`MatAutocomplete` on `CompaniesService.search`), a removable chip for an active `sourceRunId` ("Discovery run …").
- Columns: checkbox (header checkbox = select this page), name (link), email + a small verification badge (`Verified` green, `Risky` amber, `Invalid` red — shape + text, not colour alone), company, title, source, status, created.
- Selection bar above the table when `selection.count > 0`: "N selected · Clear". Buttons **Create target profile** (enabled in F14; here `disabled` with tooltip "Available with lead discovery") and **Add to campaign** (F24). They are real `<button>`s wired to `output`-style methods that F14/F24 fill in — leaving them out until their feature avoids dead UI: **render them only when the corresponding feature flag in `environment.features` is on** (`discovery`, `campaigns`, default false).
- Empty states: no leads at all (CTA: Add lead / Import CSV once F16 exists) vs no results for the current filters ("Clear filters"). Loading uses `mat-progress-bar`, errors a retry button.
- "Add lead" moves into a `MatDialog` (`AddLeadDialog`: name, email, title, company name/domain) so the table gets the page. Duplicate/suppressed (409) shown inline in the dialog.
- Add `MatPaginatorModule`, `MatSortModule`, `MatSelectModule`, `MatAutocompleteModule`, `MatCheckboxModule`, `MatChipsModule`, `MatDialogModule`, `MatProgressBarModule`, `MatTooltipModule` to `SharedModule.MATERIAL`.

**`features/leads/lead-detail/lead-detail.ts` / `.html`** (modified) — keep "Generate draft" exactly as built; add:
- Header: name, status chip, verification badge. Info card: email, title, **company** (name, domain, industry, country), **source** (`Manual` / `CSV import` / `Discovery run` with a link to `/leads?sourceRunId=<id>` when set), created/updated.
- **Status history placeholder** card: "Status history will appear here once campaigns are running." (real timeline arrives with F25 events).
- **Edit** toggles a reactive form (name, title, status limited to the legal next states via `allowedStatuses(current)` — a small pure function mirroring `LeadStatusRules`, unit-tested — company name, company domain) → `PUT`; 409 shows the server's message.
- **Delete** → `MatDialog` confirm ("This removes the lead from your list. Campaign history is kept.") → `DELETE` → back to `/leads`.
- Back link preserves list state (`router.navigate(['/leads'], { queryParamsHandling: 'preserve' })` using the list's last query kept in `LeadSelectionService.lastQuery`).

**`features/leads/lead-status.ts`** (new) — `allowedStatuses(current: LeadStatus): LeadStatus[]` (same table as `LeadStatusRules.CanTransition`; suppressed statuses → only itself).

**`environments/`** — Angular 21 projects may not have `environment.ts` by default; if absent, create `src/environments/environment.ts` with `features: { discovery: false, campaigns: false }` and wire `fileReplacements` only if a prod value differs. F14/F24 flip the flag in the same PR as the feature.

### Tests
- `leads.service.spec.ts`: query-param building (omits empty, includes all set), `PagedResult` mapping.
- `leads-list.spec.ts`: URL → query → service call; changing the filter resets `page` to 1; debounce; selection persists across a page change; header checkbox selects exactly the current page's ids; empty state variants.
- `lead-status.spec.ts`: table of allowed transitions equals the backend rule (copy the same cases as `LeadStatusRulesTests`).
- `lead-detail.spec.ts`: existing "Generate draft" tests untouched; new: edit saves via `PUT`; delete confirm → `DELETE` → navigates; source link shown only when `sourceRunId` present.
- Backend: `LeadRepositorySearchTests` (SQLite `TestDb`): paging totals, deterministic order across pages, each filter, `%`/`_` in `q` treated literally, search matches company name, **cross-workspace**: a search from workspace A never returns B's rows, `ListIdsAsync` honours `max`. `LeadQueryValidatorTests`. Controller test updated to the paged shape.

## Not in this feature
Importing from the list (F16 adds the "Import CSV" button), target-profile and campaign actions (F14/F24 fill the selection bar), saved views, column chooser, export.

## Verification
`dotnet test`, `ng test`. Seed ~50k leads with a throwaway SQL/`dotnet script` loop in a dev DB and confirm: list loads < 1 s, page 2000 works, sort by each column, `q=acme` finds by name/email/company, `q=%` returns only literal-percent matches, copy the URL into a new tab and see the same view, select across two pages then open a lead and come back — selection intact.
