import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { LeadSelectionService } from '../../../features/leads/lead-selection.service';
import { OpsService } from '../ops.service';
import { WorkspaceService } from '../workspace.service';
import { InboxUnreadService } from '../../../features/inbox/inbox-unread.service';
import { Subscription } from 'rxjs';

const TENANT_NAV = [
  { label: 'Leads', icon: 'people', link: '/leads' },
  { label: 'Campaigns', icon: 'campaign', link: '/campaigns' },
  { label: 'Inbox', icon: 'inbox', link: '/inbox' },
  { label: 'Analytics', icon: 'insights', link: '/analytics' }
];

const ADMIN_NAV = [{ label: 'Workspaces', icon: 'domain', link: '/admin/workspaces' }];

@Component({
  selector: 'app-shell',
  standalone: false,
  templateUrl: './shell.html',
  styleUrl: './shell.css'
})
export class Shell implements OnInit, OnDestroy {
  workspaceName: string | null = null;
  isMobile = false;
  canOpenDashboard = false;
  /** Unread inbox threads (F27); polled by InboxUnreadService while the tab is visible. */
  unreadCount = 0;
  private unreadSub?: Subscription;

  isSuperAdmin = false;
  homeLink = '/leads';
  navItems: { label: string; icon: string; link: string }[] = TENANT_NAV;

  constructor(
    private auth: AuthService,
    private router: Router,
    private workspace: WorkspaceService,
    private breakpoints: BreakpointObserver,
    private cdr: ChangeDetectorRef,
    private selection: LeadSelectionService,
    private ops: OpsService,
    private inboxUnread: InboxUnreadService
  ) {}

  ngOnInit(): void {
    // UI hint only: the API rejects tenant calls from the super admin anyway (403), so don't make them
    this.isSuperAdmin = this.auth.isSuperAdmin();
    this.homeLink = this.auth.homeRoute();
    if (this.isSuperAdmin) {
      this.navItems = ADMIN_NAV;
      this.workspaceName = 'Platform admin';
    } else {
      this.workspace.current().subscribe({
        next: w => { this.workspaceName = w.name; this.cdr.markForCheck(); },
        error: () => {}
      });
      this.unreadSub = this.inboxUnread.count$.subscribe(count => { this.unreadCount = count; this.cdr.markForCheck(); });
    }
    this.ops.status().subscribe({
      next: s => { this.canOpenDashboard = s.canOpenDashboard; this.cdr.markForCheck(); },
      error: () => {}
    });
    this.breakpoints.observe('(max-width: 800px)').subscribe(r => { this.isMobile = r.matches; this.cdr.markForCheck(); });
  }

  ngOnDestroy(): void { this.unreadSub?.unsubscribe(); }

  /** Badge count for a nav item (only the Inbox has one); 0 hides it. */
  badgeFor(link: string): number { return link === '/inbox' ? this.unreadCount : 0; }

  navLabel(item: { label: string; link: string }): string | null {
    const n = this.badgeFor(item.link);
    return n > 0 ? `${item.label}, ${n} unread` : null;
  }

  openJobDashboard(): void {
    this.ops.openDashboard().subscribe({ error: () => {} });
  }

  logout(): void {
    this.auth.logout();
    this.selection.clear();
    this.router.navigate(['/login']);
  }
}
