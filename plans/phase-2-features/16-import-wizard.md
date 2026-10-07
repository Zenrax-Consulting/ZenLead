# Feature 16 — Leads UI: Import Wizard

**Branch:** `feature/angular-import-wizard`
**Sprint:** 1
**Depends on:** F15 (API), F13 (`LeadsModule`, list page for the entry point and the "view imported leads" link), F12 (shared module).

## Goal
A Material stepper — **Upload → Map columns → Review → Progress → Result** — over the F15 endpoints, with auto-guessed mapping, status polling every 2 s, and a result summary with the error-CSV download. The import runs server-side, so the user can leave the page and come back (`/leads/import?batch=<id>` resumes at the progress step).

## Files to add/modify

### `features/leads/import/import.models.ts` (new)
```ts
export type ImportField = 'name' | 'firstName' | 'lastName' | 'email' | 'title' | 'companyName' | 'companyDomain' | 'industry' | 'country' | 'companySize';
export type ColumnMapping = Partial<Record<ImportField, string | null>>;

export interface UploadResponse { batchId: string; headers: string[]; sampleRows: string[][]; rowCount: number; suggestedMapping: ColumnMapping; }
export type ImportStatus = 'Uploaded' | 'Parsing' | 'Completed' | 'Failed';
export interface ImportStatusResponse {
  id: string; fileName: string; status: ImportStatus; rowCount: number; processedRowCount: number;
  importedCount: number; skippedDuplicateCount: number; skippedSuppressedCount: number; errorCount: number;
  failureReason: string | null; percent: number;
}
export const IMPORT_FIELDS: { key: ImportField; label: string; required?: boolean }[] = [
  { key: 'email', label: 'Email', required: true },
  { key: 'name', label: 'Full name' }, { key: 'firstName', label: 'First name' }, { key: 'lastName', label: 'Last name' },
  { key: 'title', label: 'Job title' }, { key: 'companyName', label: 'Company' }, { key: 'companyDomain', label: 'Company domain / website' },
  { key: 'industry', label: 'Industry' }, { key: 'country', label: 'Country' }, { key: 'companySize', label: 'Company size' }
];
```

### `features/leads/import/column-guess.ts` (new) — client-side port of the server `ColumnGuesser`
```ts
const SYNONYMS: Record<ImportField, string[]> = {
  email: ['email', 'e-mail', 'email address', 'work email', 'business email', 'emailaddress'],
  name: ['name', 'full name', 'fullname', 'contact name', 'contact'],
  firstName: ['first name', 'firstname', 'given name', 'forename'],
  lastName: ['last name', 'lastname', 'surname', 'family name'],
  title: ['title', 'job title', 'jobtitle', 'position', 'role'],
  companyName: ['company', 'company name', 'organization', 'organisation', 'account', 'employer'],
  companyDomain: ['domain', 'company domain', 'website', 'url', 'web site', 'company website'],
  industry: ['industry', 'sector'],
  country: ['country', 'country/region', 'location country'],
  companySize: ['size', 'company size', 'employees', 'headcount', '# employees']
};

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9#/ ]+/g, ' ').replace(/\s+/g, ' ').trim();

/** First header whose normalised text matches a synonym wins; a header is used for at most one field; Name beats First/Last only if both can't be found. */
export function guessMapping(headers: string[]): ColumnMapping {
  const used = new Set<string>();
  const result: ColumnMapping = {};
  for (const field of Object.keys(SYNONYMS) as ImportField[]) {
    const match = headers.find(h => !used.has(h) && SYNONYMS[field].includes(norm(h)));
    if (match) { result[field] = match; used.add(match); }
  }
  if (result.name && (result.firstName || result.lastName)) { delete result.firstName; delete result.lastName; } // server rejects both
  return result;
}
```
Keep the synonym table identical to `ColumnGuesser.cs` (F15); the server's suggestion is what the UI uses on first render, the client version re-runs only if the user clicks **Auto-detect** again, so a mismatch is cosmetic, not a bug.

### `features/leads/import/import.service.ts` (new)
```ts
@Injectable({ providedIn: 'root' })
export class ImportService {
  constructor(private http: HttpClient) {}

  upload(file: File): Observable<UploadResponse> {
    const form = new FormData(); form.append('file', file, file.name);
    return this.http.post<UploadResponse>('/api/v1/leads/import', form);       // no manual Content-Type: the browser sets the boundary
  }
  start(batchId: string, mapping: ColumnMapping): Observable<void> {
    return this.http.post<void>(`/api/v1/leads/import/${batchId}/start`, mapping);
  }
  status(batchId: string): Observable<ImportStatusResponse> {
    return this.http.get<ImportStatusResponse>(`/api/v1/leads/import/${batchId}/status`);
  }
  /** Authenticated download: a plain <a href> would not carry the in-memory bearer token. */
  downloadErrors(batchId: string): Observable<Blob> {
    return this.http.get(`/api/v1/leads/import/${batchId}/errors`, { responseType: 'blob' });
  }
}
```

### `features/leads/import/import-wizard.ts` / `.html` / `.css` (new, declared in `LeadsModule`)
State machine (plain fields + `cdr.markForCheck()`; zoneless):
```ts
export class ImportWizard implements OnInit, OnDestroy {
  @ViewChild(MatStepper) stepper!: MatStepper;

  file: File | null = null;
  upload: UploadResponse | null = null;
  mapping: ColumnMapping = {};
  status: ImportStatusResponse | null = null;
  uploading = false; starting = false; error: string | null = null;
  private poll?: Subscription;

  static readonly MAX_BYTES = 10 * 1024 * 1024;

  ngOnInit(): void {
    const batch = this.route.snapshot.queryParamMap.get('batch');
    if (batch) this.resumeProgress(batch);                       // came back to a running import
  }

  onFileChosen(file: File | null): void {
    this.error = null;
    if (!file) return;
    if (!file.name.toLowerCase().endsWith('.csv')) return this.fail('Please choose a .csv file.');
    if (file.size > ImportWizard.MAX_BYTES) return this.fail('That file is larger than 10 MB.');
    this.file = file; this.uploading = true;
    this.importService.upload(file).subscribe({
      next: r => { this.upload = r; this.mapping = { ...r.suggestedMapping }; this.uploading = false; this.stepper.next(); this.cdr.markForCheck(); },
      error: err => { this.uploading = false; this.fail(ImportWizard.messageFor(err)); }
    });
  }

  get emailMapped(): boolean { return !!this.mapping.email; }
  get nameConflict(): boolean { return !!this.mapping.name && !!(this.mapping.firstName || this.mapping.lastName); }
  get canContinueFromMapping(): boolean { return this.emailMapped && !this.nameConflict; }

  confirm(): void {                                              // Review step "Start import"
    this.starting = true;
    this.importService.start(this.upload!.batchId, this.compactMapping()).subscribe({
      next: () => { this.starting = false; this.router.navigate([], { queryParams: { batch: this.upload!.batchId }, replaceUrl: true }); this.stepper.next(); this.startPolling(this.upload!.batchId); },
      error: err => { this.starting = false; this.fail(ImportWizard.messageFor(err)); }
    });
  }

  private startPolling(batchId: string): void {
    this.poll?.unsubscribe();
    this.poll = timer(0, 2000).pipe(
      switchMap(() => this.importService.status(batchId)),
      takeWhile(s => s.status === 'Parsing' || s.status === 'Uploaded', true)   // include the final emission
    ).subscribe({
      next: s => { this.status = s; if (s.status === 'Completed' || s.status === 'Failed') this.stepper.next(); this.cdr.markForCheck(); },
      error: () => this.fail('Lost connection while checking progress. The import keeps running — reload to see it.')
    });
  }

  downloadErrors(): void {
    this.importService.downloadErrors(this.status!.id).subscribe(blob => {
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a'); a.href = url; a.download = `import-issues-${this.status!.id}.csv`; a.click();
      URL.revokeObjectURL(url);
    });
  }

  ngOnDestroy(): void { this.poll?.unsubscribe(); }

  static messageFor(err: { status?: number; error?: { detail?: string; title?: string; problems?: string[] } }): string {
    if (err.status === 413) return 'That file is larger than 10 MB.';
    if (err.status === 429) return 'Too many uploads — wait a minute and try again.';
    return err.error?.problems?.join(' ') ?? err.error?.detail ?? err.error?.title ?? 'Something went wrong. Please try again.';
  }
}
```
Template outline (`<mat-stepper [linear]="true" #stepper>`):
1. **Upload** — drop zone + `<input type="file" accept=".csv,text/csv" hidden #picker>` with a visible *Choose file* button (keyboard-accessible; `(dragover)`/`(drop)` handlers), file name/size, spinner while uploading, hint text ("CSV, up to 10 MB / 50,000 rows. Duplicates, invalid emails and unsubscribed contacts are skipped.").
2. **Map columns** — one row per `IMPORT_FIELDS` entry: label (+ "required"), `mat-select` of the file's headers with a "— don't import —" option, and the first sample value as helper text. **Auto-detect** button re-runs `guessMapping`. Below: a 5-row preview table showing mapped columns only. Inline errors: email unmapped; name vs first/last conflict. *Continue* enabled by `canContinueFromMapping`.
3. **Review** — "N rows in *file.csv* will be processed", the mapping as a read-only list, the skip rules, **Start import** / Back.
4. **Progress** — `mat-progress-bar` (`determinate`, `status.percent`), "Processed X of Y rows", live imported/duplicate/invalid counters, "You can leave this page; the import continues." with a link back to `/leads`.
5. **Result** — Completed: big numbers (**Imported**, **Duplicates**, **Unsubscribed/bounced skipped**, **Invalid**), **Download issues CSV** (shown only when duplicates+suppressed+invalid > 0), **View imported leads** → `/leads?sourceRunId=<batchId>` (CSV batches reuse `Lead.SourceRunId`), **Import another file**. Failed: `failureReason` and *Try again*.

Edge cases handled in the component: re-choosing a file after a failed upload resets state; browser back during progress keeps the poll alive only while the component exists; `status.status === 'Uploaded'` on resume (user mapped nothing yet) restarts at step 2 using a `GET` of headers — **not supported in this feature**: resume only handles `Parsing`/`Completed`/`Failed`; an `Uploaded` batch shows "This upload wasn't started — upload the file again."

### Wiring
- **`features/leads/leads-module.ts`** — declare `ImportWizard`; add route `{ path: 'import', component: ImportWizard }` **before** `{ path: ':id', … }`.
- **`features/leads/leads-list/leads-list.html`** — header button **Import CSV** (`routerLink="/leads/import"`).
- **`shared/shared-module.ts`** — add `MatStepperModule`, `MatSelectModule` (already from F13), `MatProgressBarModule` (F13).
- **`src/proxy.conf.js`** — no change (`/api/**`).

## Tests (PBI 16.2 — targeted)
- **`column-guess.spec.ts`** — table-driven: `"E-mail Address"`, `"Work Email"`, `"First Name"/"Last Name"`, `"Job Title"`, `"Company Name"`, `"Website"`, `"# Employees"`; a header is never assigned to two fields; unknown headers stay unmapped; `Name` + `First/Last` collapses to `Name`; case/punctuation/whitespace insensitive.
- **`import-wizard.spec.ts`** (with `HttpTestingController` and fake timers) — rejects non-`.csv` and >10 MB **before** any request; successful upload pre-fills the mapping from `suggestedMapping` and advances; *Continue* disabled until email is mapped and while name conflicts; `start` payload omits unmapped fields; polling every 2 s stops after `Completed`/`Failed` and on destroy; `?batch=` resumes at progress; error-download button hidden when there are no issues; server `problems[]` shown on a 400.
- **`import.service.spec.ts`** — upload sends `FormData` with no explicit `Content-Type`; `downloadErrors` uses `responseType: 'blob'`.

## Not in this feature
Drag-reordering of columns, saving a mapping as a template, re-mapping a started import, Excel files, an "import history" list (the batch id in the URL is the only way back in Phase 2).

## Verification
`ng test`, `ng build`. Manually with the F15 verification file set: upload a 10k-row `;`-delimited file → mapping auto-filled, missing email shows the blocking error, review → start → watch the bar advance, leave to `/leads` and return via the browser back button (`?batch=` resumes), result numbers reconcile with the server, error CSV downloads and opens cleanly, **View imported leads** shows exactly the imported rows with source "CSV import".
