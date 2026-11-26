import {
  ChangeDetectorRef, Component, ElementRef, EventEmitter, Input, OnChanges, OnDestroy, OnInit, Output, SimpleChanges, ViewChild
} from '@angular/core';
import { FormBuilder, FormControl, FormGroup, Validators } from '@angular/forms';
import { Subscription, debounceTime, distinctUntilChanged } from 'rxjs';
import { Lead } from '../../leads/leads.models';
import { LeadsService } from '../../leads/leads.service';
import { Campaign, PreviewResponse, Step, StepRequest } from '../campaigns.models';
import { CampaignsService } from '../campaigns.service';
import { stepSendLabel } from '../schedule-display';
import { TOKENS, insertAt, requiredTokens, unknownTokens } from '../token-insert';

export type SaveState = 'idle' | 'saving' | 'saved' | 'error';
type TemplateField = 'subjectTemplate' | 'bodyTemplate';

/** Autosave delay after the last keystroke (blur saves at once). */
export const AUTOSAVE_MS = 800;

@Component({
  selector: 'app-step-editor',
  standalone: false,
  templateUrl: './step-editor.html',
  styleUrl: './step-editor.css'
})
export class StepEditor implements OnInit, OnChanges, OnDestroy {
  @Input({ required: true }) step!: Step;
  @Input({ required: true }) campaign!: Campaign;
  @Input() index = 0;
  @Input() count = 1;
  @Input() day = 0;
  @Input() locked = false;
  @Input() reorderLocked = false;
  @Output() saved = new EventEmitter<Step>();
  @Output() deleted = new EventEmitter<void>();
  @Output() move = new EventEmitter<number>();

  @ViewChild('subjectInput') subjectInput?: ElementRef<HTMLInputElement>;
  @ViewChild('bodyInput') bodyInput?: ElementRef<HTMLTextAreaElement>;

  readonly tokens = TOKENS;
  form: FormGroup<{
    delayDays: FormControl<number>;
    subjectTemplate: FormControl<string>;
    bodyTemplate: FormControl<string>;
    useAiPersonalisation: FormControl<boolean>;
  }>;

  saveState: SaveState = 'idle';
  saveError: string | null = null;
  /** Changes not yet sent to the server. */
  dirty = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private saveAgain = false;
  private lastField: TemplateField = 'bodyTemplate';

  // preview with a real lead
  leadControl = new FormControl<string | Lead>('', { nonNullable: true });
  leadOptions: Lead[] = [];
  previewLead: Lead | null = null;
  preview: PreviewResponse | null = null;
  previewError: string | null = null;
  private previewAfterSave = false;

  // AI draft
  aiInstruction = new FormControl('', { nonNullable: true });
  aiLoading = false;
  aiError: string | null = null;

  private subs = new Subscription();

  constructor(
    fb: FormBuilder,
    private service: CampaignsService,
    private leads: LeadsService,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.nonNullable.group({
      delayDays: [0, [Validators.required, Validators.min(0), Validators.max(90)]],
      subjectTemplate: ['', [Validators.required, Validators.maxLength(300)]],
      bodyTemplate: ['', [Validators.required, Validators.maxLength(10000)]],
      useAiPersonalisation: [false]
    });
  }

  ngOnInit(): void {
    this.patchFromStep();
    this.subs.add(this.form.valueChanges.subscribe(() => this.scheduleSave()));
    this.subs.add(this.leadControl.valueChanges.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      if (typeof value !== 'string' || !value.trim()) { this.leadOptions = []; return; }
      this.leads.list({ page: 1, pageSize: 10, q: value.trim() }).subscribe({
        next: r => { this.leadOptions = r.items; this.cdr.markForCheck(); },
        error: () => {}
      });
    }));
  }

  ngOnChanges(changes: SimpleChanges): void {
    // A server-side change (e.g. the new first step's delay reset to 0) is shown unless the user is mid-edit.
    if (changes['step'] && !changes['step'].firstChange && !this.dirty && this.saveState !== 'saving') this.patchFromStep();
    if (changes['index']) this.syncDelayLock();
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    if (this.timer) clearTimeout(this.timer);
  }

  private patchFromStep(): void {
    const s = this.step;
    this.form.setValue({
      delayDays: this.isFirst ? 0 : s.delayDays, subjectTemplate: s.subjectTemplate, bodyTemplate: s.bodyTemplate,
      useAiPersonalisation: s.useAiPersonalisation
    }, { emitEvent: false });
    this.syncDelayLock();
  }

  private syncDelayLock(): void {
    const delay = this.form.controls.delayDays;
    if (this.isFirst) { delay.disable({ emitEvent: false }); } else { delay.enable({ emitEvent: false }); }
  }

  get isFirst(): boolean { return this.index === 0; }

  get sendLabel(): string {
    const c = this.campaign;
    return stepSendLabel(this.day, c.windowStart, c.windowEnd, c.sendDays);
  }

  get unknown(): string[] {
    const v = this.form.getRawValue();
    return unknownTokens(`${v.subjectTemplate}\n${v.bodyTemplate}`);
  }

  get usesCompanyWithoutFallback(): boolean {
    const v = this.form.getRawValue();
    return requiredTokens(`${v.subjectTemplate}\n${v.bodyTemplate}`).includes('company');
  }

  // ---- autosave ----

  private scheduleSave(): void {
    this.dirty = true;
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.save(), AUTOSAVE_MS);
  }

  /** Blur saves straight away (also flushes an AI draft, which doesn't start the debounce). */
  onBlur(): void { if (this.dirty) this.save(); }

  save(): void {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    if (this.saveState === 'saving') { this.saveAgain = true; return; }
    if (this.form.invalid) {
      this.saveState = 'error';
      this.saveError = 'Fill in a subject and a body (delay 0–90 days).';
      this.cdr.markForCheck();
      return;
    }
    const v = this.form.getRawValue();
    const request: StepRequest = {
      delayDays: this.isFirst ? 0 : v.delayDays, subjectTemplate: v.subjectTemplate, bodyTemplate: v.bodyTemplate,
      useAiPersonalisation: v.useAiPersonalisation
    };
    this.dirty = false;
    this.saveState = 'saving';
    this.saveError = null;
    this.cdr.markForCheck();
    this.service.updateStep(this.campaign.id, this.step.id, request).subscribe({
      next: step => {
        this.saveState = 'saved';
        this.saved.emit(step);
        this.afterSave();
      },
      error: e => {
        this.dirty = true;
        this.saveState = 'error';
        this.saveError = e.status === 400 ? (e.error?.errors ? firstError(e.error.errors) : e.error?.detail) ?? 'The server rejected this step.' : null;
        this.previewAfterSave = false;
        this.saveAgain = false;
        this.cdr.markForCheck();
      }
    });
  }

  private afterSave(): void {
    if (this.saveAgain) { this.saveAgain = false; this.save(); return; }
    if (this.previewAfterSave) { this.previewAfterSave = false; this.runPreview(); }
    this.cdr.markForCheck();
  }

  // ---- tokens ----

  /** "{{name}}" — built in code because a literal double brace in the template would be read as interpolation. */
  braces(name: string): string { return `{{${name}}}`; }

  tokenTooltip(token: string): string {
    return `Inserts ${token} at the cursor. Add a fallback for leads without a value: ${this.braces('firstName|there')}.`;
  }

  rememberField(field: TemplateField): void { this.lastField = field; }

  insertToken(token: string): void {
    const field = this.lastField;
    const el = (field === 'subjectTemplate' ? this.subjectInput : this.bodyInput)?.nativeElement;
    const control = this.form.controls[field];
    const value = control.value ?? '';
    const start = el?.selectionStart ?? value.length;
    const end = el?.selectionEnd ?? value.length;
    const result = insertAt(value, start, end, token);
    control.setValue(result.value);
    queueMicrotask(() => { el?.focus(); el?.setSelectionRange(result.caret, result.caret); });
  }

  // ---- preview ----

  leadLabel = (value: string | Lead | null): string => (value && typeof value !== 'string' ? value.name : '');

  onLeadSelected(lead: Lead): void {
    this.previewLead = lead;
    this.runPreview();
  }

  /** The preview renders the saved step on the server, so pending edits are saved first. */
  runPreview(): void {
    const lead = this.previewLead;
    if (!lead) return;
    if (this.dirty || this.saveState === 'saving') {
      this.previewAfterSave = true;
      if (this.dirty) this.save();
      return;
    }
    this.previewError = null;
    this.service.preview(this.campaign.id, lead.id, this.step.id).subscribe({
      next: p => { this.preview = p; this.cdr.markForCheck(); },
      error: () => { this.previewError = 'Could not render the preview.'; this.cdr.markForCheck(); }
    });
  }

  missingLabel(token: string): string {
    switch (token.toLowerCase()) {
      case 'company': return 'This lead has no company — the email would be skipped.';
      case 'title': return 'This lead has no job title — the email would be skipped.';
      default: return `This lead has no value for {{${token}}} — the email would be skipped.`;
    }
  }

  // ---- AI draft ----

  /** Fills subject/body from the AI draft for the preview lead; nothing is saved until the next blur (or Save now). */
  generateWithAi(): void {
    const lead = this.previewLead;
    if (!lead || this.aiLoading) return;
    this.aiLoading = true;
    this.aiError = null;
    this.leads.composeEmail(lead.id, this.aiInstruction.value.trim() || undefined).subscribe({
      next: draft => {
        this.aiLoading = false;
        this.form.patchValue({ subjectTemplate: draft.subject, bodyTemplate: draft.body }, { emitEvent: false });
        this.dirty = true;
        this.saveState = 'idle';
        this.cdr.markForCheck();
      },
      error: e => {
        this.aiLoading = false;
        this.aiError = e.status === 429 && e.error?.code === 'ai_budget_exceeded'
          ? (e.error.message ?? 'This workspace\'s monthly AI budget has been used.')
          : e.status === 429 ? 'Too many draft requests right now. Please wait a minute and try again.' : 'Could not generate a draft.';
        this.cdr.markForCheck();
      }
    });
  }
}

function firstError(errors: Record<string, string[]>): string | undefined {
  return Object.values(errors).flat()[0];
}
