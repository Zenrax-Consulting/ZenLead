import { AfterViewInit, ChangeDetectorRef, Component, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatStepper } from '@angular/material/stepper';
import { Observable, Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { guessMapping } from './column-guess';
import {
  ColumnMapping, IMPORT_FIELDS, ImportField, ImportStatusResponse, UploadResponse, isTerminalImport, issueCount
} from './import.models';
import { ImportService } from './import.service';

export const POLL_INTERVAL_MS = 2000;
export const MAX_BYTES = 10 * 1024 * 1024;

export enum WizardStep { Upload = 0, Map = 1, Review = 2, Progress = 3, Result = 4 }

/** Emits the status immediately and then every 2 s, completing after the first terminal state (which is emitted). */
export function pollImport(service: Pick<ImportService, 'status'>, batchId: string, intervalMs = POLL_INTERVAL_MS): Observable<ImportStatusResponse> {
  return timer(0, intervalMs).pipe(
    switchMap(() => service.status(batchId)),
    takeWhile(s => !isTerminalImport(s), true));
}

interface HttpErrorLike {
  status?: number;
  error?: unknown;
}

@Component({
  selector: 'app-import-wizard',
  standalone: false,
  templateUrl: './import-wizard.html',
  styleUrl: './import-wizard.css'
})
export class ImportWizard implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild(MatStepper) stepper?: MatStepper;

  readonly fields = IMPORT_FIELDS;
  readonly Step = WizardStep;

  step = WizardStep.Upload;
  file: File | null = null;
  upload: UploadResponse | null = null;
  mapping: ColumnMapping = {};
  status: ImportStatusResponse | null = null;
  uploading = false;
  starting = false;
  resuming = false;
  dragging = false;
  error: string | null = null;
  pollError: string | null = null;
  private poll?: Subscription;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private importService: ImportService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    const batch = this.route.snapshot.queryParamMap.get('batch');
    if (batch) this.resume(batch);
  }

  ngAfterViewInit(): void { this.syncStepper(); }

  ngOnDestroy(): void { this.poll?.unsubscribe(); }

  // ---- step navigation (the component's `step` is the source of truth; the stepper follows it) ----

  goTo(step: WizardStep): void {
    this.step = step;
    this.syncStepper();
    this.cdr.markForCheck();
  }

  /** Header clicks can only go back to an editable, completed step; keep `step` in line. */
  onStepperChange(index: number): void {
    if (index === this.step) return;
    this.step = index;
    this.syncStepper();
  }

  private syncStepper(): void {
    const stepper = this.stepper;
    if (!stepper?.steps) return;
    stepper.steps.forEach((s, i) => (s.completed = i < this.step));
    if (stepper.selectedIndex !== this.step) stepper.selectedIndex = this.step;
  }

  // ---- step 1: upload ----

  onDragOver(e: DragEvent): void {
    e.preventDefault();
    this.dragging = true;
  }

  onDrop(e: DragEvent): void {
    e.preventDefault();
    this.dragging = false;
    this.onFileChosen(e.dataTransfer?.files?.[0] ?? null);
  }

  onFileChosen(file: File | null): void {
    this.error = null;
    if (!file || this.uploading) return;
    if (!file.name.toLowerCase().endsWith('.csv')) return this.fail('Please choose a .csv file.');
    if (file.size > MAX_BYTES) return this.fail('That file is larger than 10 MB.');

    this.file = file;
    this.upload = null;
    this.mapping = {};
    this.uploading = true;
    this.importService.upload(file).subscribe({
      next: r => {
        this.upload = r;
        this.mapping = { ...r.suggestedMapping };
        this.uploading = false;
        this.goTo(WizardStep.Map);
      },
      error: err => { this.uploading = false; this.fail(ImportWizard.messageFor(err)); }
    });
  }

  // ---- step 2: map columns ----

  get headers(): string[] { return this.upload?.headers ?? []; }

  setMapping(field: ImportField, header: string | null): void {
    this.mapping = { ...this.mapping, [field]: header };
  }

  autoDetect(): void { this.mapping = guessMapping(this.headers); }

  get emailMapped(): boolean { return !!this.mapping.email; }
  get nameConflict(): boolean { return !!this.mapping.name && !!(this.mapping.firstName || this.mapping.lastName); }

  /** Headers chosen for more than one field (the server rejects these). */
  get duplicateHeaders(): string[] {
    const counts = new Map<string, number>();
    for (const h of Object.values(this.mapping)) if (h) counts.set(h, (counts.get(h) ?? 0) + 1);
    return [...counts].filter(([, n]) => n > 1).map(([h]) => h);
  }

  get canContinueFromMapping(): boolean { return this.emailMapped && !this.nameConflict && this.duplicateHeaders.length === 0; }

  get mappedFields(): typeof IMPORT_FIELDS { return this.fields.filter(f => !!this.mapping[f.key]); }

  get previewRows(): string[][] { return (this.upload?.sampleRows ?? []).slice(0, 5); }

  cell(row: string[], header: string | null | undefined): string {
    if (!header) return '';
    const i = this.headers.indexOf(header);
    return i >= 0 ? row[i] ?? '' : '';
  }

  /** First non-empty sample value for a header, shown as helper text under its select. */
  sampleFor(header: string | null | undefined): string {
    return this.previewRows.map(r => this.cell(r, header)).find(v => v.trim() !== '') ?? '';
  }

  continueFromMapping(): void {
    if (this.canContinueFromMapping) this.goTo(WizardStep.Review);
  }

  // ---- step 3: review & start ----

  /** Only mapped fields are sent; the API treats absent fields as unmapped. */
  compactMapping(): ColumnMapping {
    const result: ColumnMapping = {};
    for (const [field, header] of Object.entries(this.mapping) as [ImportField, string | null | undefined][])
      if (header) result[field] = header;
    return result;
  }

  confirm(): void {
    const upload = this.upload;
    if (!upload || this.starting || !this.canContinueFromMapping) return;
    this.error = null;
    this.starting = true;
    this.importService.start(upload.batchId, this.compactMapping()).subscribe({
      next: () => {
        this.starting = false;
        this.router.navigate([], { relativeTo: this.route, queryParams: { batch: upload.batchId }, replaceUrl: true });
        this.goTo(WizardStep.Progress);
        this.startPolling(upload.batchId);
      },
      error: err => { this.starting = false; this.fail(ImportWizard.messageFor(err)); }
    });
  }

  // ---- steps 4–5: progress & result ----

  private resume(batchId: string): void {
    this.resuming = true;
    this.importService.status(batchId).subscribe({
      next: s => {
        this.resuming = false;
        this.status = s;
        if (s.status === 'Uploaded') {
          this.status = null;
          this.clearBatchParam();
          this.fail('This upload wasn\'t started — upload the file again.');
        } else if (isTerminalImport(s)) {
          this.goTo(WizardStep.Result);
        } else {
          this.goTo(WizardStep.Progress);
          this.startPolling(batchId);
        }
      },
      error: err => {
        this.resuming = false;
        this.clearBatchParam();
        this.fail(err?.status === 404 ? 'That import could not be found.' : ImportWizard.messageFor(err));
      }
    });
  }

  startPolling(batchId: string): void {
    this.poll?.unsubscribe();
    this.pollError = null;
    this.poll = pollImport(this.importService, batchId).subscribe({
      next: s => {
        this.status = s;
        if (isTerminalImport(s)) this.goTo(WizardStep.Result);
        else this.cdr.markForCheck();
      },
      error: () => {
        this.pollError = 'Lost connection while checking progress. The import keeps running on the server.';
        this.cdr.markForCheck();
      }
    });
  }

  get issues(): number { return this.status ? issueCount(this.status) : 0; }

  downloadErrors(): void {
    const id = this.status?.id;
    if (!id) return;
    this.importService.downloadErrors(id).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `import-issues-${id}.csv`;
        a.click();
        setTimeout(() => URL.revokeObjectURL(url));
      },
      error: () => this.fail('Could not download the issues file. Please try again.')
    });
  }

  startOver(): void {
    this.poll?.unsubscribe();
    this.file = null;
    this.upload = null;
    this.mapping = {};
    this.status = null;
    this.error = null;
    this.pollError = null;
    this.clearBatchParam();
    this.goTo(WizardStep.Upload);
  }

  // ---- helpers ----

  private clearBatchParam(): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: { batch: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private fail(message: string): void {
    this.error = message;
    this.cdr.markForCheck();
  }

  static messageFor(err: HttpErrorLike | null | undefined): string {
    if (err?.status === 413) return 'That file is larger than 10 MB.';
    if (err?.status === 429) return 'Too many uploads — wait a minute and try again.';
    if (err?.status === 0) return 'Could not reach the server. Check your connection and try again.';
    const body = err?.error;
    if (body && typeof body === 'object') {
      const problem = body as { detail?: string; title?: string; errors?: Record<string, string[]> };
      const problems = problem.errors ? Object.values(problem.errors).flat() : [];
      if (problems.length > 0) return problems.join(' ');
      if (problem.detail) return problem.detail;
      if (problem.title) return problem.title;
    }
    return 'Something went wrong. Please try again.';
  }
}
