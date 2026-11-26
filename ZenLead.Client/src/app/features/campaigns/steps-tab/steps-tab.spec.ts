import { ChangeDetectorRef, ElementRef, SimpleChange } from '@angular/core';
import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { Campaign, CampaignStatus, Step } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { StepsTab } from './steps-tab';

const step = (id: string, order: number): Step =>
  ({ id, order, delayDays: order === 1 ? 0 : 3, subjectTemplate: 'S', bodyTemplate: 'B', useAiPersonalisation: false });

const campaign = (status: CampaignStatus = 'Draft', stepsStarted = 0): Campaign => ({
  id: 'c1', name: 'Spring', status, fromName: null, dailySendCap: 30, windowStart: '09:00', windowEnd: '17:00', sendDays: 31,
  timeZoneId: null, effectiveTimeZone: 'UTC', workspaceTimeZone: 'UTC', createdAt: '', activatedAt: null,
  steps: [step('s1', 1), step('s2', 2), step('s3', 3)],
  counts: { total: 0, active: 0, completed: 0, replied: 0, unsubscribed: 0, bounced: 0, failed: 0 }, stepsStarted
});

const dropEvent = (previousIndex: number, currentIndex: number) => ({ previousIndex, currentIndex }) as CdkDragDrop<Step[]>;

describe('StepsTab', () => {
  let service: { reorderSteps: ReturnType<typeof vi.fn>; get: ReturnType<typeof vi.fn> };
  let snack: { open: ReturnType<typeof vi.fn> };

  const build = (c: Campaign) => {
    const tab = new StepsTab(service as unknown as CampaignsService, snack as unknown as MatSnackBar, {} as MatDialog,
      new ElementRef(document.createElement('div')), { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    tab.campaign = c;
    tab.ngOnChanges({ campaign: new SimpleChange(undefined, c, true) });
    return tab;
  };

  beforeEach(() => {
    service = { reorderSteps: vi.fn(), get: vi.fn() };
    snack = { open: vi.fn() };
  });

  it('drag to a new index sends the new id order', () => {
    service.reorderSteps.mockReturnValue(of([step('s2', 1), step('s3', 2), step('s1', 3)]));
    const tab = build(campaign());

    tab.drop(dropEvent(0, 2));

    expect(service.reorderSteps).toHaveBeenCalledWith('c1', ['s2', 's3', 's1']);
    expect(tab.steps.map(s => s.id)).toEqual(['s2', 's3', 's1']);
  });

  it('the Move down button reorders the same way', () => {
    service.reorderSteps.mockReturnValue(of([]));
    const tab = build(campaign());

    tab.move(1, 1);

    expect(service.reorderSteps).toHaveBeenCalledWith('c1', ['s1', 's3', 's2']);
  });

  it('rejects a drop involving a sent step with a snackbar and no request', () => {
    const tab = build(campaign('Active', 1));

    expect(tab.locked(0)).toBe(true);
    expect(tab.locked(1)).toBe(false);
    tab.drop(dropEvent(2, 0));

    expect(snack.open).toHaveBeenCalledWith('Steps that have already been sent can’t be reordered.', 'OK', expect.anything());
    expect(service.reorderSteps).not.toHaveBeenCalled();
    expect(tab.steps.map(s => s.id)).toEqual(['s1', 's2', 's3']);
  });

  it('reloads the list when the server answers 409', () => {
    const fresh = campaign('Active', 2);
    service.reorderSteps.mockReturnValue(throwError(() => ({ status: 409, error: { message: 'Steps can\'t be reordered once leads have been sent the first step.' } })));
    service.get.mockReturnValue(of(fresh));
    const tab = build(campaign());
    const emitted: Campaign[] = [];
    tab.campaignChange.subscribe(c => emitted.push(c));

    tab.drop(dropEvent(1, 2));

    expect(service.get).toHaveBeenCalledWith('c1');
    expect(emitted).toContain(fresh);
    expect(snack.open).toHaveBeenCalledWith('Steps can\'t be reordered once leads have been sent the first step.', 'OK', expect.anything());
  });
});
