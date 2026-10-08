import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { WorkspaceService } from '../workspace.service';

@Component({
  selector: 'app-shell',
  standalone: false,
  templateUrl: './shell.html',
  styleUrl: './shell.css'
})
export class Shell implements OnInit {
  workspaceName: string | null = null;
  isMobile = false;

  readonly navItems = [
    { label: 'Leads', icon: 'people', link: '/leads' },
    { label: 'Campaigns', icon: 'campaign', link: '/campaigns' },
    { label: 'Inbox', icon: 'inbox', link: '/inbox' },
    { label: 'Analytics', icon: 'insights', link: '/analytics' }
  ];

  constructor(
    private auth: AuthService,
    private router: Router,
    private workspace: WorkspaceService,
    private breakpoints: BreakpointObserver,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.workspace.current().subscribe({
      next: w => { this.workspaceName = w.name; this.cdr.markForCheck(); },
      error: () => {}
    });
    this.breakpoints.observe('(max-width: 800px)').subscribe(r => { this.isMobile = r.matches; this.cdr.markForCheck(); });
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
