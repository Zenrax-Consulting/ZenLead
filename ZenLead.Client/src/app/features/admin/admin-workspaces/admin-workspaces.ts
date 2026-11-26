import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AdminService } from '../admin.service';
import { WorkspaceAdmin } from '../admin.models';
import { CreateWorkspaceDialog } from '../create-workspace-dialog/create-workspace-dialog';

@Component({
  selector: 'app-admin-workspaces',
  standalone: false,
  templateUrl: './admin-workspaces.html',
  styleUrl: './admin-workspaces.css'
})
export class AdminWorkspaces implements OnInit {
  readonly columns = ['name', 'adminEmail', 'timeZone', 'userCount', 'createdAt'];
  workspaces: WorkspaceAdmin[] = [];
  loading = false;
  errorMessage: string | null = null;

  constructor(
    private admin: AdminService,
    private dialog: MatDialog,
    private snackBar: MatSnackBar,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.errorMessage = null;
    this.admin.list().subscribe({
      next: list => { this.workspaces = list; this.loading = false; this.cdr.markForCheck(); },
      error: () => { this.loading = false; this.errorMessage = 'Could not load workspaces.'; this.cdr.markForCheck(); }
    });
  }

  openCreate(): void {
    this.dialog.open(CreateWorkspaceDialog, { autoFocus: 'first-tabbable' }).afterClosed().subscribe(created => {
      if (!created) return;
      this.snackBar.open('Workspace created. We emailed the admin.', 'OK', { duration: 4000 });
      this.load();
    });
  }
}
