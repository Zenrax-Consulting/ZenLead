import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ValidationErrors, Validators } from '@angular/forms';
import { MatChipInputEvent } from '@angular/material/chips';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { ProfileDraftService } from '../profile-draft.service';
import { Criteria, TargetProfile } from '../profiles.models';
import { ProfilesService } from '../profiles.service';

export type ChipField = 'jobTitles' | 'industries' | 'countries' | 'companyDomains';

export const MAX_CHIPS = 20;
export const MAX_CHIP_LENGTH = 100;

/** Same rules as the server: at most 20 items, each at most 100 characters. */
export function chipListValidator(control: AbstractControl<string[]>): ValidationErrors | null {
  const list = control.value ?? [];
  return list.length > MAX_CHIPS || list.some(v => v.length > MAX_CHIP_LENGTH) ? { chipList: true } : null;
}

export function sizeRangeValidator(group: AbstractControl): ValidationErrors | null {
  const min = group.get('companySizeMin')?.value as number | null;
  const max = group.get('companySizeMax')?.value as number | null;
  return min !== null && max !== null && min > max ? { sizeRange: true } : null;
}

@Component({
  selector: 'app-target-profile-edit',
  standalone: false,
  templateUrl: './target-profile-edit.html',
  styleUrl: './target-profile-edit.css'
})
export class TargetProfileEdit implements OnInit {
  readonly chipFields: { key: ChipField; label: string; hint: string }[] = [
    { key: 'jobTitles', label: 'Job titles', hint: 'e.g. CTO, Head of Sales' },
    { key: 'industries', label: 'Industries', hint: 'e.g. Software' },
    { key: 'countries', label: 'Countries', hint: 'e.g. US, GB' },
    { key: 'companyDomains', label: 'Company domains', hint: 'e.g. acme.com' }
  ];
  readonly separators = [13, 188];       // Enter, comma

  readonly form = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    jobTitles: new FormControl<string[]>([], { nonNullable: true, validators: [chipListValidator] }),
    industries: new FormControl<string[]>([], { nonNullable: true, validators: [chipListValidator] }),
    countries: new FormControl<string[]>([], { nonNullable: true, validators: [chipListValidator] }),
    companyDomains: new FormControl<string[]>([], { nonNullable: true, validators: [chipListValidator] }),
    companySizeMin: new FormControl<number | null>(null, [Validators.min(0)]),
    companySizeMax: new FormControl<number | null>(null, [Validators.min(0)])
  }, { validators: [sizeRangeValidator] });

  id: string | null = null;
  draftBanner: string | null = null;
  loading = false;
  saving = false;
  errorMessage: string | null = null;
  private sourceLeadId: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private profiles: ProfilesService,
    private drafts: ProfileDraftService,
    private snackBar: MatSnackBar,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.id = this.route.snapshot.paramMap.get('id');
    if (this.id) { this.loadExisting(this.id); return; }

    const draft = this.drafts.take();
    if (draft) {
      this.sourceLeadId = draft.sourceLeadId;
      this.applyCriteria(draft.name, draft.criteria);
      this.draftBanner = `Suggested from ${draft.sourceLeadId ? 'a lead' : 'your selected leads'}; review and save.`;
    } else if (this.route.snapshot.queryParamMap.get('draft')) {
      this.snackBar.open('The suggested profile was lost on refresh — start again from the leads list.', 'OK', { duration: 6000 });
    }
  }

  private loadExisting(id: string): void {
    this.loading = true;
    this.profiles.list().subscribe({
      next: all => {
        const found = all.find(p => p.id === id);
        if (found) this.apply(found); else this.errorMessage = 'Target profile not found.';
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: () => { this.errorMessage = 'Failed to load the profile.'; this.loading = false; this.cdr.markForCheck(); }
    });
  }

  private apply(p: TargetProfile): void {
    this.sourceLeadId = p.sourceLeadId;
    this.applyCriteria(p.name, p.criteria);
  }

  private applyCriteria(name: string, c: Criteria): void {
    this.form.reset({
      name, jobTitles: c.jobTitles, industries: c.industries, countries: c.countries,
      companyDomains: c.companyDomains, companySizeMin: c.companySizeMin, companySizeMax: c.companySizeMax
    });
  }

  list(field: ChipField): string[] { return this.form.controls[field].value; }

  addChip(field: ChipField, raw: string): void {
    const value = raw.trim();
    if (!value) return;
    const current = this.list(field);
    if (current.some(v => v.toLowerCase() === value.toLowerCase())) return;
    this.form.controls[field].setValue([...current, value]);
  }

  onChipInput(field: ChipField, event: MatChipInputEvent): void {
    this.addChip(field, event.value);
    event.chipInput.clear();
  }

  removeChip(field: ChipField, value: string): void {
    this.form.controls[field].setValue(this.list(field).filter(v => v !== value));
  }

  /** The server rejects a profile with no criteria: it would pull arbitrary people and burn credits. */
  get hasCriterion(): boolean {
    const v = this.form.getRawValue();
    return v.jobTitles.length + v.industries.length + v.countries.length + v.companyDomains.length > 0
      || v.companySizeMin !== null || v.companySizeMax !== null;
  }

  get canSave(): boolean { return this.form.valid && this.hasCriterion && !this.saving; }

  save(): void {
    if (!this.canSave) return;
    this.saving = true;
    this.errorMessage = null;
    const v = this.form.getRawValue();
    const request = {
      name: v.name.trim(),
      criteria: {
        jobTitles: v.jobTitles, industries: v.industries, countries: v.countries, companyDomains: v.companyDomains,
        companySizeMin: v.companySizeMin, companySizeMax: v.companySizeMax
      }
    };
    const call = this.id ? this.profiles.update(this.id, request) : this.profiles.create(request);
    call.subscribe({
      next: () => { this.saving = false; this.router.navigate(['/leads/profiles']); },
      error: err => {
        this.saving = false;
        this.errorMessage = err.status === 400 ? 'Some values are not valid. Check the fields and try again.' : 'Failed to save the profile.';
        this.cdr.markForCheck();
      }
    });
  }
}
