import { ChangeDetectorRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { CampaignsModule } from '../campaigns-module';
import { ActivationPreview, Campaign, CampaignStatus } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { ActivationConfirmDialog, ActivationProblemsDialog, SenderHelpDialog } from '../dialogs/dialogs';
import { CampaignDetail } from './campaign-detail';

const campaign = (status: CampaignStatus = 'Draft'): Campaign => ({
  id: 'c1', name: 'Spring', status, fromName: null, dailySendCap: 30, windowStart: '09:00', windowEnd: '17:00', sendDays: 31,
  timeZoneId: null, effectiveTimeZone: 'UTC', workspaceTimeZone: 'UTC', createdAt: '', activatedAt: null,
  steps: [{ id: 's1', order: 1, delayDays: 0, subjectTemplate: 'S', bodyTemplate: 'B', useAiPersonalisation: true }],
  counts: { total: 3, active: 3, completed: 0, replied: 0, unsubscribed: 0, bounced: 0, failed: 0 }, stepsStarted: 0
});

const preview = (aiSteps: number): ActivationPreview =>
  ({ enrolledActive: 3, aiSteps, estimatedAiCalls: 3 * aiSteps, estimatedAiCostUsd: 0.04, aiBudgetRemainingUsd: 19 });

describe('CampaignDetail', () => {
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialogResults: Map<unknown, unknown>;
  let dialog: { open: ReturnType<typeof vi.fn> };
  let navigate: ReturnType<typeof vi.fn>;

  const build = (c = campaign(), tab: string | null = null) => {
    service = {
      get: vi.fn(() => of(c)), senderStatus: vi.fn(() => of({ fromAddress: 'a@x.test', fromName: 'Z', domainVerified: false, inboundDomain: 'r.x.test' })),
      activationPreview: vi.fn(() => of(preview(1))), activate: vi.fn(), pause: vi.fn(() => of(campaign('Paused')))
    };
    dialogResults = new Map();
    dialog = { open: vi.fn((type: unknown) => ({ afterClosed: () => of(dialogResults.get(type)) })) };
    navigate = vi.fn();
    const route = {
      paramMap: new BehaviorSubject(convertToParamMap({ id: 'c1' })),
      queryParamMap: new BehaviorSubject(convertToParamMap(tab ? { tab } : {}))
    };
    const detail = new CampaignDetail(route as unknown as ActivatedRoute, { navigate } as unknown as Router,
      service as unknown as CampaignsService, dialog as unknown as MatDialog, { open: vi.fn() } as unknown as MatSnackBar,
      { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    detail.ngOnInit();
    return detail;
  };

  it('reads the active tab from ?tab=', () => {
    expect(build(campaign(), 'settings').selectedIndex).toBe(2);
    expect(build(campaign(), 'leads').selectedIndex).toBe(1);
    expect(build(campaign(), 'bogus').selectedIndex).toBe(0);
  });

  it('shows Activate for Draft/Paused and Pause for Active, neither for Completed', () => {
    const states: [CampaignStatus, boolean, boolean][] = [['Draft', true, false], ['Paused', true, false], ['Active', false, true], ['Completed', false, false]];
    for (const [status, canActivate, canPause] of states) {
      const detail = build(campaign(status));
      expect([detail.canActivate, detail.canPause]).toEqual([canActivate, canPause]);
    }
  });

  it('activate → 409 problems opens the problems dialog, and Fix selects the Steps tab and that step', () => {
    const detail = build(campaign(), 'settings');
    const problems = [{ code: 'empty_subject', message: 'Step 1 has no subject.', stepOrder: 1 }];
    service['activate'].mockReturnValue(throwError(() => ({ status: 409, error: { code: 'activation_blocked', message: 'x', problems } })));
    dialogResults.set(ActivationConfirmDialog, true);
    dialogResults.set(ActivationProblemsDialog, { action: 'fix', stepOrder: 1 });

    detail.activate();

    expect(service['activationPreview']).toHaveBeenCalledWith('c1');
    expect(dialog.open).toHaveBeenCalledWith(ActivationProblemsDialog, expect.objectContaining({ data: problems }));
    expect(detail.selectedIndex).toBe(0);
    expect(detail.focusStep).toEqual({ order: 1 });
    expect(navigate.mock.calls.at(-1)![1].queryParams).toEqual({ tab: null });
    expect(detail.busy).toBe(false);
  });

  it('the domain_unverified problem opens the sender help', () => {
    const detail = build();
    service['activate'].mockReturnValue(throwError(() => ({ status: 409, error: { problems: [{ code: 'domain_unverified', message: 'Not verified.' }] } })));
    dialogResults.set(ActivationConfirmDialog, true);
    dialogResults.set(ActivationProblemsDialog, { action: 'sender-help' });

    detail.activate();

    expect(dialog.open).toHaveBeenCalledWith(SenderHelpDialog, expect.objectContaining({ data: expect.objectContaining({ domainVerified: false }) }));
  });

  it('cancelling the confirm dialog does not activate', () => {
    const detail = build();
    dialogResults.set(ActivationConfirmDialog, false);

    detail.activate();

    expect(service['activate']).not.toHaveBeenCalled();
    expect(detail.busy).toBe(false);
  });

  it('a successful activation updates the campaign', () => {
    const detail = build();
    service['activate'].mockReturnValue(of(campaign('Active')));
    dialogResults.set(ActivationConfirmDialog, true);

    detail.activate();

    expect(detail.campaign?.status).toBe('Active');
    expect(detail.canPause).toBe(true);
  });

  it('pause updates the campaign', () => {
    const detail = build(campaign('Active'));
    detail.pause();
    expect(service['pause']).toHaveBeenCalledWith('c1');
    expect(detail.campaign?.status).toBe('Paused');
  });
});

describe('ActivationConfirmDialog', () => {
  const render = async (aiSteps: number) => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [CampaignsModule],
      providers: [
        provideRouter([]),
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
        { provide: MAT_DIALOG_DATA, useValue: { campaignName: 'Spring', preview: preview(aiSteps), timeline: 'Day 0 → Day 3', window: 'Mon–Fri, 09:00–17:00 (UTC)' } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivationConfirmDialog);
    fixture.detectChanges();
    return fixture.nativeElement.textContent as string;
  };

  it('shows the AI estimate only when a step uses AI', async () => {
    const withAi = await render(2);
    expect(withAi).toContain('~6 AI calls, about $0.04');
    expect(withAi).toContain('budget left $19.00');
    expect(withAi).toContain('Day 0 → Day 3');

    expect(await render(0)).not.toContain('AI calls');
  });
});

describe('ActivationProblemsDialog', () => {
  it('finds the step a problem is about', () => {
    const dialog = new ActivationProblemsDialog([], { close: vi.fn() } as unknown as MatDialogRef<ActivationProblemsDialog>);
    expect(dialog.stepOf({ code: 'bad_token', message: 'x', stepOrder: 3 })).toBe(3);
    expect(dialog.stepOf({ code: 'empty_body', message: 'Step 2 has no body.' })).toBe(2);
    expect(dialog.stepOf({ code: 'no_steps', message: 'Add at least one step.' })).toBe(1);
    expect(dialog.stepOf({ code: 'window_invalid', message: 'Send window is invalid.' })).toBeNull();
  });
});
