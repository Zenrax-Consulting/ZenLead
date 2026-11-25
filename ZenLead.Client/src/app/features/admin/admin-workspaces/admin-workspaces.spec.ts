import { ChangeDetectorRef } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { AdminService } from '../admin.service';
import { WorkspaceAdmin } from '../admin.models';
import { CreateWorkspaceDialog, supportedTimeZones } from '../create-workspace-dialog/create-workspace-dialog';
import { AdminWorkspaces } from './admin-workspaces';

const zenrax: WorkspaceAdmin = {
  id: 'w1', name: 'Zenrax', adminEmail: 'admin@zenrax.test', timeZone: 'UTC', userCount: 0, createdAt: '2026-11-26T00:00:00Z'
};
const cdr = { markForCheck: vi.fn() } as unknown as ChangeDetectorRef;

describe('AdminWorkspaces', () => {
  const build = (list: () => unknown, dialogResult: WorkspaceAdmin | undefined = undefined) => {
    const admin = { list: vi.fn(list), create: vi.fn() };
    const dialog = { open: vi.fn(() => ({ afterClosed: () => of(dialogResult) })) };
    const snackBar = { open: vi.fn() };
    const component = new AdminWorkspaces(
      admin as unknown as AdminService, dialog as unknown as MatDialog, snackBar as unknown as MatSnackBar, cdr);
    return { component, admin, dialog, snackBar };
  };

  it('loads the workspace list on init', () => {
    const { component } = build(() => of([zenrax]));
    component.ngOnInit();
    expect(component.workspaces).toEqual([zenrax]);
    expect(component.loading).toBe(false);
  });

  it('shows an error when the list fails', () => {
    const { component } = build(() => throwError(() => ({ status: 500 })));
    component.ngOnInit();
    expect(component.errorMessage).toBe('Could not load workspaces.');
  });

  it('reloads and confirms after the dialog creates a workspace', () => {
    const { component, admin, dialog, snackBar } = build(() => of([]), zenrax);
    component.ngOnInit();

    component.openCreate();

    expect(dialog.open).toHaveBeenCalledWith(CreateWorkspaceDialog, expect.anything());
    expect(admin.list).toHaveBeenCalledTimes(2);
    expect(snackBar.open).toHaveBeenCalledWith('Workspace created. We emailed the admin.', 'OK', expect.anything());
  });

  it('does nothing when the dialog is cancelled', () => {
    const { component, admin, snackBar } = build(() => of([]), undefined);
    component.ngOnInit();
    component.openCreate();
    expect(admin.list).toHaveBeenCalledTimes(1);
    expect(snackBar.open).not.toHaveBeenCalled();
  });
});

describe('CreateWorkspaceDialog', () => {
  const build = (create: () => unknown) => {
    const admin = { create: vi.fn(create) };
    const ref = { close: vi.fn() };
    const component = new CreateWorkspaceDialog(
      new FormBuilder(), admin as unknown as AdminService, ref as unknown as MatDialogRef<CreateWorkspaceDialog, WorkspaceAdmin | undefined>, cdr);
    return { component, admin, ref };
  };

  it('does not post an invalid form', () => {
    const { component, admin } = build(() => of(zenrax));
    component.form.patchValue({ name: 'Z', adminEmail: 'nope' });
    component.submit();
    expect(admin.create).not.toHaveBeenCalled();
  });

  it('posts the trimmed name and closes with the created workspace', () => {
    const { component, admin, ref } = build(() => of(zenrax));
    component.form.setValue({ name: '  Zenrax ', adminEmail: 'admin@zenrax.test', timeZone: 'Asia/Dubai' });

    component.submit();

    expect(admin.create).toHaveBeenCalledWith({ name: 'Zenrax', adminEmail: 'admin@zenrax.test', timeZone: 'Asia/Dubai' });
    expect(ref.close).toHaveBeenCalledWith(zenrax);
  });

  it('explains a duplicate name (409) and stays open', () => {
    const { component, ref } = build(() => throwError(() => ({ status: 409 })));
    component.form.setValue({ name: 'Zenrax', adminEmail: 'admin@zenrax.test', timeZone: 'UTC' });

    component.submit();

    expect(component.errorMessage).toBe('A workspace with that name already exists.');
    expect(component.submitting).toBe(false);
    expect(ref.close).not.toHaveBeenCalled();
  });

  it('offers UTC as a time zone', () => {
    expect(supportedTimeZones()).toContain('UTC');
  });
});
