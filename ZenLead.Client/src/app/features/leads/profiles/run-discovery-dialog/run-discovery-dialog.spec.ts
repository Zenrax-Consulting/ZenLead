import { ChangeDetectorRef } from '@angular/core';
import { MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { DiscoveryRun, RunStatus } from '../profiles.models';
import { ProfilesService } from '../profiles.service';
import { POLL_INTERVAL_MS, RunDiscoveryDialog, pollRun } from './run-discovery-dialog';

const run = (status: RunStatus, imported = 0): DiscoveryRun => ({
  id: 'r1', targetProfileId: 'p1', provider: 'Fake', status, requestedCount: 20, foundCount: 30, importedCount: imported,
  skippedDuplicateCount: 5, skippedSuppressedCount: 1, noEmailCount: 2, creditsUsed: 30, failureReason: null,
  createdAt: '2026-10-08T00:00:00Z', finishedAt: null
});

describe('pollRun', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('polls every 2 seconds and stops after the first terminal state', async () => {
    const states: RunStatus[] = ['Queued', 'Running', 'Running', 'Completed', 'Completed'];
    let calls = 0;
    const service = { getRun: () => of(run(states[Math.min(calls++, states.length - 1)])) };
    const seen: RunStatus[] = [];
    let completed = false;

    pollRun(service, 'r1').subscribe({ next: r => seen.push(r.status), complete: () => (completed = true) });
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 10);

    expect(seen).toEqual(['Queued', 'Running', 'Running', 'Completed']);
    expect(completed).toBe(true);
    expect(calls).toBe(4);                      // no requests after the terminal state
  });

  it('stops polling when unsubscribed', async () => {
    let calls = 0;
    const service = { getRun: () => of(run('Running', calls++)) };

    const sub = pollRun(service, 'r1').subscribe();
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS);
    const before = calls;
    sub.unsubscribe();
    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 5);

    expect(calls).toBe(before);
  });
});

describe('RunDiscoveryDialog', () => {
  const close = vi.fn();
  const navigate = vi.fn();
  const profile = { id: 'p1', name: 'US CTOs', sourceLeadId: null, createdAt: '', criteria: { jobTitles: [], industries: [], countries: ['US'], companySizeMin: null, companySizeMax: null, companyDomains: [] } };
  const credits = { used: 120, cap: 500, remaining: 380, providerRemaining: null, maxLeadsPerRun: 100 };

  const build = (service: Partial<ProfilesService>, c = credits) =>
    new RunDiscoveryDialog(
      { profile, credits: c }, { close } as unknown as MatDialogRef<RunDiscoveryDialog>, service as ProfilesService,
      { navigate } as unknown as Router, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);

  beforeEach(() => { close.mockReset(); navigate.mockReset(); vi.useFakeTimers(); });
  afterEach(() => vi.useRealTimers());

  it('limits the max-leads input to 1..maxLeadsPerRun', () => {
    const d = build({});
    d.maxControl.setValue(0);
    expect(d.maxControl.invalid).toBe(true);
    d.maxControl.setValue(101);
    expect(d.maxControl.invalid).toBe(true);
    d.maxControl.setValue(100);
    expect(d.maxControl.valid).toBe(true);
  });

  it('starts a run, shows live progress, and stops polling at a terminal state', async () => {
    const states = [run('Running', 3), run('Completed', 12)];
    let polls = 0;
    const d = build({ startRun: vi.fn(() => of(run('Queued'))), getRun: () => of(states[Math.min(polls++, 1)]) });
    d.maxControl.setValue(20);

    d.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(d.run?.status).toBe('Running');
    expect(d.inProgress).toBe(true);

    await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 5);
    expect(d.run?.status).toBe('Completed');
    expect(d.finished).toBe(true);
    expect(polls).toBe(2);
    d.ngOnDestroy();
  });

  it('links the summary to the leads list filtered by the run', () => {
    const d = build({});
    d.run = run('Completed', 12);

    d.viewImported();

    expect(navigate).toHaveBeenCalledWith(['/leads'], { queryParams: { sourceRunId: 'r1' } });
    expect(close).toHaveBeenCalled();
  });

  it.each([
    ['Completed', 'Completed'],
    ['CapReached', 'Cap reached']
  ] as [RunStatus, string][])('labels %s as "%s"', (status, label) => {
    const d = build({});
    d.run = run(status);
    expect(d.statusLabel).toBe(label);
  });

  it('shows the failure reason', () => {
    const d = build({});
    d.run = { ...run('Failed'), failureReason: 'Provider credits exhausted.' };
    expect(d.statusLabel).toBe('Failed — Provider credits exhausted.');
  });

  it('refuses to start when the monthly cap is exhausted', () => {
    const startRun = vi.fn();
    const d = build({ startRun }, { ...credits, used: 500, remaining: 0 });

    d.start();

    expect(d.capped).toBe(true);
    expect(startRun).not.toHaveBeenCalled();
  });

  it('shows the API message when a run is already in progress', () => {
    const d = build({ startRun: () => throwError(() => ({ status: 409, error: { message: 'A discovery run is already in progress.' } })) });

    d.start();

    expect(d.errorMessage).toBe('A discovery run is already in progress.');
    expect(d.starting).toBe(false);
  });
});
