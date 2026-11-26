import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Subject, of } from 'rxjs';
import { CampaignsSharedModule } from '../campaigns-shared-module';
import { EnrollResult } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { EnrollDialog, EnrollDialogData } from './enroll-dialog';

const result = (over: Partial<EnrollResult> = {}): EnrollResult => ({
  enrolled: 8, alreadyEnrolled: 2, skippedSuppressed: 1, skippedInvalidEmail: 1, skippedMissingTokens: 3, notFound: 0,
  warnUnverified: 4, warnRisky: 2, truncated: false, ...over
});

describe('EnrollDialog', () => {
  let enroll: ReturnType<typeof vi.fn>;
  let close: ReturnType<typeof vi.fn>;
  let fixture: ComponentFixture<EnrollDialog>;

  const setup = async (dryRun: EnrollResult, data: Partial<EnrollDialogData> = {}) => {
    enroll = vi.fn((_id: string, _t: unknown, isDryRun: boolean) => of(isDryRun ? dryRun : { ...dryRun }));
    close = vi.fn();
    await TestBed.configureTestingModule({
      imports: [CampaignsSharedModule],
      providers: [
        { provide: CampaignsService, useValue: { enroll } },
        { provide: MatDialogRef, useValue: { close } },
        { provide: MAT_DIALOG_DATA, useValue: { campaignId: 'c1', campaignName: 'Spring', leadIds: ['a', 'b'], total: 2, ...data } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(EnrollDialog);
    fixture.detectChanges();
    return fixture.componentInstance;
  };

  const confirmButton = () =>
    [...fixture.nativeElement.querySelectorAll('button')].find((b: HTMLButtonElement) => b.textContent!.includes('Add')) as HTMLButtonElement;

  it('runs a dry run on open and renders the breakdown', async () => {
    await setup(result());

    expect(enroll).toHaveBeenCalledWith('c1', { leadIds: ['a', 'b'] }, true);
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Will be added: 8');
    expect(text).toContain('Already in this campaign: 2');
    expect(text).toContain('Skipped — unsubscribed or bounced: 1');
    expect(text).toContain('Skipped — invalid email: 1');
    expect(text).toContain('Skipped — missing a value used in your templates: 3');
  });

  it('warns about unverified and risky emails', async () => {
    await setup(result());

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('4 have unverified emails');
    expect(text).toContain('2 are risky');
    expect(text).toContain('These may bounce and hurt deliverability.');
  });

  it('disables Confirm when nothing would be added', async () => {
    const dialog = await setup(result({ enrolled: 0, alreadyEnrolled: 2 }));

    expect(dialog.canConfirm).toBe(false);
    expect(confirmButton().disabled).toBe(true);
    dialog.confirm();
    expect(enroll).toHaveBeenCalledTimes(1);                   // only the dry run
  });

  it('disables Confirm while the dry run is loading', async () => {
    const pending = new Subject<EnrollResult>();
    enroll = vi.fn(() => pending);
    await TestBed.configureTestingModule({
      imports: [CampaignsSharedModule],
      providers: [
        { provide: CampaignsService, useValue: { enroll } },
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
        { provide: MAT_DIALOG_DATA, useValue: { campaignId: 'c1', campaignName: 'Spring', leadIds: ['a'], total: 1 } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(EnrollDialog);
    fixture.detectChanges();

    expect(confirmButton().disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Checking which leads can be added');
  });

  it('confirm sends dryRun:false and closes with the result', async () => {
    const dialog = await setup(result(), { leadIds: undefined, filter: { page: 1, pageSize: 25, q: 'acme' }, total: 40 });

    dialog.confirm();

    expect(enroll).toHaveBeenLastCalledWith('c1', { filter: { page: 1, pageSize: 25, q: 'acme' } }, false);
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ enrolled: 8 }));
  });

  it('says when only the first 5,000 were considered', async () => {
    await setup(result({ truncated: true }));
    expect(fixture.nativeElement.textContent).toContain('Only the first 5,000 were considered.');
  });
});
