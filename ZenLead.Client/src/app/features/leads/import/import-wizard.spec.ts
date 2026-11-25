import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ImportStatus, ImportStatusResponse, UploadResponse } from './import.models';
import { ImportService } from './import.service';
import { ImportWizard, MAX_BYTES, POLL_INTERVAL_MS, WizardStep, pollImport } from './import-wizard';

const upload: UploadResponse = {
  batchId: 'b1',
  headers: ['Email', 'Name', 'First Name', 'Company'],
  sampleRows: [['a@x.com', 'Ann', 'Ann', 'Acme'], ['b@x.com', 'Bob', 'Bob', 'Beta']],
  rowCount: 2,
  suggestedMapping: { email: 'Email', firstName: 'First Name', companyName: 'Company' }
};

const status = (s: ImportStatus, extra: Partial<ImportStatusResponse> = {}): ImportStatusResponse => ({
  id: 'b1', fileName: 'leads.csv', status: s, rowCount: 100, processedRowCount: 50, importedCount: 40,
  skippedDuplicateCount: 0, skippedSuppressedCount: 0, errorCount: 0, failureReason: null, percent: 50, ...extra
});

const csv = (name = 'leads.csv', size = 100) => {
  const f = new File(['x'], name, { type: 'text/csv' });
  Object.defineProperty(f, 'size', { value: size });
  return f;
};

describe('pollImport', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('polls every 2 seconds and stops after the first terminal state', async () => {
    const states: ImportStatus[] = ['Parsing', 'Parsing', 'Completed', 'Completed'];
    let calls = 0;
    const service = { status: () => of(status(states[Math.min(calls++, states.length - 1)])) };
    const seen: ImportStatus[] = [];

    pollImport(service, 'b1').subscribe(s => seen.push(s.status));
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 10);

    expect(seen).toEqual(['Parsing', 'Parsing', 'Completed']);
    expect(calls).toBe(3);
  });

  it('stops after Failed', async () => {
    let calls = 0;
    const service = { status: () => of(status(calls++ === 0 ? 'Parsing' : 'Failed')) };
    pollImport(service, 'b1').subscribe();
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 10);
    expect(calls).toBe(2);
  });
});

describe('ImportWizard', () => {
  const navigate = vi.fn();
  let queryParams: Record<string, string>;

  const build = (service: Partial<ImportService>) =>
    new ImportWizard(
      { snapshot: { get queryParamMap() { return convertToParamMap(queryParams); } } } as unknown as ActivatedRoute,
      { navigate } as unknown as Router,
      service as ImportService,
      { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);

  beforeEach(() => { navigate.mockReset(); queryParams = {}; vi.useFakeTimers(); });
  afterEach(() => vi.useRealTimers());

  it.each([
    ['leads.xlsx', 100, 'Please choose a .csv file.'],
    ['leads.csv', MAX_BYTES + 1, 'That file is larger than 10 MB.']
  ])('rejects %s (%i bytes) before any request', (name, size, message) => {
    const uploadFn = vi.fn();
    const w = build({ upload: uploadFn });

    w.onFileChosen(csv(name, size));

    expect(uploadFn).not.toHaveBeenCalled();
    expect(w.error).toBe(message);
    expect(w.step).toBe(WizardStep.Upload);
  });

  it('pre-fills the mapping from the server suggestion and advances', () => {
    const w = build({ upload: () => of(upload) });

    w.onFileChosen(csv());

    expect(w.mapping).toEqual(upload.suggestedMapping);
    expect(w.step).toBe(WizardStep.Map);
    expect(w.uploading).toBe(false);
  });

  it('shows the server message when the upload is rejected', () => {
    const w = build({ upload: () => throwError(() => ({ status: 400, error: { title: 'Bad Request', detail: 'The file has no header row.' } })) });

    w.onFileChosen(csv());

    expect(w.error).toBe('The file has no header row.');
    expect(w.step).toBe(WizardStep.Upload);
  });

  it('blocks Continue until email is mapped, and while names conflict or a header is reused', () => {
    const w = build({ upload: () => of(upload) });
    w.onFileChosen(csv());
    expect(w.canContinueFromMapping).toBe(true);

    w.setMapping('email', null);
    expect(w.emailMapped).toBe(false);
    expect(w.canContinueFromMapping).toBe(false);

    w.setMapping('email', 'Email');
    w.setMapping('name', 'Name');
    expect(w.nameConflict).toBe(true);
    expect(w.canContinueFromMapping).toBe(false);

    w.setMapping('firstName', null);
    w.setMapping('title', 'Company');
    expect(w.duplicateHeaders).toEqual(['Company']);
    expect(w.canContinueFromMapping).toBe(false);

    w.continueFromMapping();
    expect(w.step).toBe(WizardStep.Map);
  });

  it('Auto-detect re-runs the client guess', () => {
    const w = build({ upload: () => of(upload) });
    w.onFileChosen(csv());
    w.mapping = {};

    w.autoDetect();

    expect(w.mapping).toEqual({ email: 'Email', firstName: 'First Name', companyName: 'Company' });
  });

  it('starts with only mapped fields, records the batch in the URL and polls to the result', async () => {
    const start = vi.fn(() => of(undefined));
    const states = [status('Parsing'), status('Completed', { percent: 100, processedRowCount: 100 })];
    let polls = 0;
    const w = build({ upload: () => of(upload), start, status: () => of(states[Math.min(polls++, 1)]) });
    w.onFileChosen(csv());
    w.setMapping('title', null);
    w.continueFromMapping();
    expect(w.step).toBe(WizardStep.Review);

    w.confirm();

    expect(start).toHaveBeenCalledWith('b1', { email: 'Email', firstName: 'First Name', companyName: 'Company' });
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { batch: 'b1' }, replaceUrl: true }));
    await vi.advanceTimersByTimeAsync(0);
    expect(w.step).toBe(WizardStep.Progress);
    expect(w.status?.percent).toBe(50);

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 5);
    expect(w.step).toBe(WizardStep.Result);
    expect(polls).toBe(2);
  });

  it('shows validation problems from a 400 on start', () => {
    const w = build({
      upload: () => of(upload),
      start: () => throwError(() => ({ status: 400, error: { title: 'One or more validation errors occurred.', errors: { mapping: ['An email column is required.', 'Column "X" is not in the file.'] } } }))
    });
    w.onFileChosen(csv());

    w.confirm();

    expect(w.error).toBe('An email column is required. Column "X" is not in the file.');
    expect(w.starting).toBe(false);
  });

  it('stops polling on destroy', async () => {
    let polls = 0;
    const w = build({ status: () => { polls++; return of(status('Parsing')); } });
    w.startPolling('b1');
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    const before = polls;

    w.ngOnDestroy();
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 5);

    expect(polls).toBe(before);
  });

  it('resumes at the progress step from ?batch=', async () => {
    queryParams = { batch: 'b1' };
    const w = build({ status: () => of(status('Parsing')) });

    w.ngOnInit();
    await vi.advanceTimersByTimeAsync(0);

    expect(w.step).toBe(WizardStep.Progress);
    expect(w.status?.status).toBe('Parsing');
    w.ngOnDestroy();
  });

  it('resumes a finished import at the result step', () => {
    queryParams = { batch: 'b1' };
    const w = build({ status: () => of(status('Completed')) });

    w.ngOnInit();

    expect(w.step).toBe(WizardStep.Result);
  });

  it('asks for a new upload when the resumed batch was never started', () => {
    queryParams = { batch: 'b1' };
    const w = build({ status: () => of(status('Uploaded')) });

    w.ngOnInit();

    expect(w.step).toBe(WizardStep.Upload);
    expect(w.error).toContain('upload the file again');
  });

  it('only offers the issues download when something was skipped', () => {
    const w = build({});
    w.status = status('Completed');
    expect(w.issues).toBe(0);

    w.status = status('Completed', { skippedDuplicateCount: 2, errorCount: 1 });
    expect(w.issues).toBe(3);
  });

  it.each([
    [{ status: 413 }, 'That file is larger than 10 MB.'],
    [{ status: 429 }, 'Too many uploads — wait a minute and try again.'],
    [{ status: 409, error: { detail: 'This import has already been started.' } }, 'This import has already been started.'],
    [{ status: 500, error: null }, 'Something went wrong. Please try again.']
  ])('maps %o to a message', (err, message) => {
    expect(ImportWizard.messageFor(err)).toBe(message);
  });
});
