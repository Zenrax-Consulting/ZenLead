import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Lead } from '../../leads/leads.models';
import { LeadsService } from '../../leads/leads.service';
import { CampaignsModule } from '../campaigns-module';
import { Campaign, Step } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { AUTOSAVE_MS, StepEditor } from './step-editor';

const step = (order = 2): Step =>
  ({ id: `s${order}`, order, delayDays: order === 1 ? 0 : 3, subjectTemplate: 'Hello', bodyTemplate: 'Hi {{firstName|there}}', useAiPersonalisation: false });

const campaign: Campaign = {
  id: 'c1', name: 'Spring', status: 'Draft', fromName: null, dailySendCap: 30, windowStart: '09:00', windowEnd: '17:00', sendDays: 31,
  timeZoneId: null, effectiveTimeZone: 'UTC', workspaceTimeZone: 'UTC', createdAt: '', activatedAt: null, steps: [],
  counts: { total: 0, active: 0, completed: 0, replied: 0, unsubscribed: 0, bounced: 0, failed: 0 }, stepsStarted: 0
};

const lead: Lead = {
  id: 'l1', name: 'John Roe', email: 'john@x.test', title: null, status: 'New', createdAt: '', company: null, source: 'Manual',
  sourceRunId: null, emailVerificationStatus: 'Verified'
};

describe('StepEditor', () => {
  let service: { updateStep: ReturnType<typeof vi.fn>; preview: ReturnType<typeof vi.fn> };
  let leads: { list: ReturnType<typeof vi.fn>; composeEmail: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<StepEditor>;

  const setup = async (s = step(), opts: { index?: number; locked?: boolean } = {}) => {
    service = { updateStep: vi.fn((_c: string, id: string, r: Partial<Step>) => of({ ...s, ...r, id })), preview: vi.fn() };
    leads = { list: vi.fn(() => of({ items: [], total: 0, page: 1, pageSize: 10 })), composeEmail: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [CampaignsModule],
      providers: [provideRouter([]), { provide: CampaignsService, useValue: service }, { provide: LeadsService, useValue: leads }]
    }).compileComponents();
    fixture = TestBed.createComponent(StepEditor);
    fixture.componentRef.setInput('step', s);
    fixture.componentRef.setInput('campaign', campaign);
    fixture.componentRef.setInput('index', opts.index ?? 1);
    fixture.componentRef.setInput('count', 3);
    fixture.componentRef.setInput('locked', opts.locked ?? false);
    fixture.detectChanges();
    return fixture.componentInstance;
  };

  const render = () => { fixture.componentRef.changeDetectorRef.markForCheck(); fixture.detectChanges(); return fixture.nativeElement.textContent as string; };

  afterEach(() => vi.useRealTimers());

  it('debounces autosave and sends one PUT', async () => {
    const editor = await setup();
    vi.useFakeTimers();

    editor.form.controls.subjectTemplate.setValue('Hel');
    await vi.advanceTimersByTimeAsync(AUTOSAVE_MS / 2);
    editor.form.controls.subjectTemplate.setValue('Hello there');
    await vi.advanceTimersByTimeAsync(AUTOSAVE_MS - 1);
    expect(service.updateStep).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);

    expect(service.updateStep).toHaveBeenCalledTimes(1);
    expect(service.updateStep).toHaveBeenCalledWith('c1', 's2', expect.objectContaining({ subjectTemplate: 'Hello there', delayDays: 3 }));
    expect(editor.saveState).toBe('saved');
    expect(render()).toContain('Saved');
  });

  it('warns about unknown tokens while typing', async () => {
    const editor = await setup();
    editor.form.controls.bodyTemplate.setValue('Hi {{favouriteColour}}', { emitEvent: false });

    expect(render()).toContain('Unknown token {{favouriteColour}}');
  });

  it('warns when {{company}} has no fallback', async () => {
    const editor = await setup();
    editor.form.controls.bodyTemplate.setValue('About {{company}}', { emitEvent: false });
    expect(render()).toContain('leads without a company will be skipped');

    editor.form.controls.bodyTemplate.setValue('About {{company|your team}}', { emitEvent: false });
    expect(render()).not.toContain('leads without a company will be skipped');
  });

  it('makes the first step\'s delay read-only and always sends 0', async () => {
    const editor = await setup(step(1), { index: 0 });

    expect(editor.form.controls.delayDays.disabled).toBe(true);
    expect((fixture.nativeElement.querySelector('input[type=number]') as HTMLInputElement).disabled).toBe(true);
    expect(render()).toContain('Sends right away');
    editor.save();
    expect(service.updateStep).toHaveBeenCalledWith('c1', 's1', expect.objectContaining({ delayDays: 0 }));
  });

  it('disables delete and moves on a locked (already sent) step', async () => {
    await setup(step(), { locked: true });

    const del = fixture.nativeElement.querySelector('button[aria-label="Delete step 2"]') as HTMLButtonElement;
    const up = fixture.nativeElement.querySelector('button[aria-label="Move step 2 up"]') as HTMLButtonElement;
    expect(del.disabled).toBe(true);
    expect(up.disabled).toBe(true);
  });

  it('shows missing tokens from the server preview', async () => {
    const editor = await setup();
    service.preview.mockReturnValue(of({ subject: 'Hello', body: 'Hi John at ', missingTokens: ['company'] }));

    editor.onLeadSelected(lead);

    expect(service.preview).toHaveBeenCalledWith('c1', 'l1', 's2');
    expect(render()).toContain('This lead has no company — the email would be skipped.');
  });

  it('fills the fields from an AI draft but saves only on blur', async () => {
    const editor = await setup();
    editor.previewLead = lead;
    editor.aiInstruction.setValue('mention our Dubai office');
    leads.composeEmail.mockReturnValue(of({ subject: 'AI subject', body: 'AI body', tokensUsed: 10 }));
    vi.useFakeTimers();

    editor.generateWithAi();
    await vi.advanceTimersByTimeAsync(AUTOSAVE_MS * 3);

    expect(leads.composeEmail).toHaveBeenCalledWith('l1', 'mention our Dubai office');
    expect(editor.form.controls.subjectTemplate.value).toBe('AI subject');
    expect(editor.form.controls.bodyTemplate.value).toBe('AI body');
    expect(service.updateStep).not.toHaveBeenCalled();

    editor.onBlur();
    expect(service.updateStep).toHaveBeenCalledTimes(1);
    expect(service.updateStep).toHaveBeenCalledWith('c1', 's2', expect.objectContaining({ subjectTemplate: 'AI subject', bodyTemplate: 'AI body' }));
  });

  it('shows the budget message on ai_budget_exceeded', async () => {
    const editor = await setup();
    editor.previewLead = lead;
    leads.composeEmail.mockReturnValue(throwError(() => ({ status: 429, error: { code: 'ai_budget_exceeded', message: 'The monthly AI budget has been used.' } })));

    editor.generateWithAi();

    expect(editor.aiError).toBe('The monthly AI budget has been used.');
    expect(render()).toContain('The monthly AI budget has been used.');
  });

  it('inserts a token at the caret of the last focused field', async () => {
    const editor = await setup();
    const subject = fixture.nativeElement.querySelector('input[formcontrolname="subjectTemplate"]') as HTMLInputElement;
    subject.focus();
    subject.dispatchEvent(new Event('focus'));
    subject.setSelectionRange(5, 5);

    editor.insertToken('{{firstName}}');

    expect(editor.form.controls.subjectTemplate.value).toBe('Hello{{firstName}}');
  });
});
