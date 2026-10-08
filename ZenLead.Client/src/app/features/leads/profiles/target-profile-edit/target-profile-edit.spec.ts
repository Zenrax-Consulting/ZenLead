import { ChangeDetectorRef } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { ProfileDraftService } from '../profile-draft.service';
import { emptyCriteria } from '../profiles.models';
import { ProfilesService } from '../profiles.service';
import { MAX_CHIPS, TargetProfileEdit } from './target-profile-edit';

describe('TargetProfileEdit', () => {
  const profiles = { create: vi.fn(), update: vi.fn(), list: vi.fn() };
  const navigate = vi.fn();
  const snackBar = { open: vi.fn() };
  let drafts: ProfileDraftService;

  const build = (id: string | null = null, draftQuery: string | null = null) => {
    const route = { snapshot: { paramMap: { get: () => id }, queryParamMap: { get: () => draftQuery } } } as unknown as ActivatedRoute;
    const component = new TargetProfileEdit(
      route, { navigate } as unknown as Router, profiles as unknown as ProfilesService, drafts,
      snackBar as unknown as MatSnackBar, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    component.ngOnInit();
    return component;
  };

  beforeEach(() => {
    drafts = new ProfileDraftService();
    profiles.create.mockReset().mockReturnValue(of({}));
    profiles.update.mockReset().mockReturnValue(of({}));
    profiles.list.mockReset();
    navigate.mockReset();
    snackBar.open.mockReset();
  });

  it('adds and removes chips, ignoring blanks and case-insensitive duplicates', () => {
    const c = build();

    c.addChip('jobTitles', ' CTO ');
    c.addChip('jobTitles', 'cto');
    c.addChip('jobTitles', '   ');
    c.addChip('jobTitles', 'CEO');
    expect(c.list('jobTitles')).toEqual(['CTO', 'CEO']);

    c.removeChip('jobTitles', 'CTO');
    expect(c.list('jobTitles')).toEqual(['CEO']);
  });

  it('requires at least one criterion before it can be saved', () => {
    const c = build();
    c.form.controls.name.setValue('Empty');
    expect(c.hasCriterion).toBe(false);
    expect(c.canSave).toBe(false);

    c.addChip('countries', 'US');
    expect(c.canSave).toBe(true);
  });

  it('counts a company size on its own as a criterion', () => {
    const c = build();
    c.form.controls.name.setValue('Sized');
    c.form.controls.companySizeMin.setValue(10);
    expect(c.canSave).toBe(true);
  });

  it('rejects a minimum size above the maximum and negative sizes', () => {
    const c = build();
    c.form.controls.name.setValue('Sized');
    c.form.patchValue({ companySizeMin: 500, companySizeMax: 100 });
    expect(c.form.hasError('sizeRange')).toBe(true);
    expect(c.canSave).toBe(false);

    c.form.patchValue({ companySizeMin: 50, companySizeMax: 100 });
    expect(c.form.hasError('sizeRange')).toBe(false);

    c.form.patchValue({ companySizeMin: -1 });
    expect(c.form.controls.companySizeMin.invalid).toBe(true);
  });

  it('enforces the 20-item and 100-character limits the server enforces', () => {
    const c = build();
    c.form.controls.name.setValue('Many');

    for (let i = 0; i <= MAX_CHIPS; i++) c.addChip('industries', `Industry ${i}`);
    expect(c.form.controls.industries.invalid).toBe(true);
    expect(c.canSave).toBe(false);

    c.form.controls.industries.setValue(['x'.repeat(101)]);
    expect(c.form.controls.industries.invalid).toBe(true);
  });

  it('creates a new profile and returns to the list', () => {
    const c = build();
    c.form.controls.name.setValue('  US CTOs ');
    c.addChip('countries', 'US');

    c.save();

    expect(profiles.create).toHaveBeenCalledWith({
      name: 'US CTOs',
      criteria: { jobTitles: [], industries: [], countries: ['US'], companyDomains: [], companySizeMin: null, companySizeMax: null }
    });
    expect(navigate).toHaveBeenCalledWith(['/leads/profiles']);
  });

  it('pre-fills from a suggested draft and shows the banner', () => {
    drafts.set({ name: 'Similar to Jane', sourceLeadId: 'l1', criteria: { ...emptyCriteria(), jobTitles: ['CTO'], companySizeMin: 51, companySizeMax: 200 } });

    const c = build();

    expect(c.form.controls.name.value).toBe('Similar to Jane');
    expect(c.list('jobTitles')).toEqual(['CTO']);
    expect(c.form.controls.companySizeMax.value).toBe(200);
    expect(c.draftBanner).toContain('review and save');
    expect(drafts.take()).toBeNull();           // consumed
  });

  it('tells the user when a draft was lost on refresh', () => {
    build(null, '1');
    expect(snackBar.open).toHaveBeenCalled();
  });

  it('loads and updates an existing profile', () => {
    profiles.list.mockReturnValue(of([{ id: 'p1', name: 'Mine', sourceLeadId: null, createdAt: '', criteria: { ...emptyCriteria(), industries: ['Software'] } }]));

    const c = build('p1');
    expect(c.form.controls.name.value).toBe('Mine');
    c.save();

    expect(profiles.update).toHaveBeenCalledWith('p1', expect.objectContaining({ name: 'Mine' }));
    expect(profiles.create).not.toHaveBeenCalled();
  });
});
