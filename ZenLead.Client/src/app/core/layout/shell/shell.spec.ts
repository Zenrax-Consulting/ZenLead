import { TestBed } from '@angular/core/testing';
import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { Shell } from './shell';
import { AppModule } from '../../../app-module';
import { AuthService } from '../../auth/auth.service';
import { OpsService } from '../ops.service';
import { WorkspaceService } from '../workspace.service';

describe('Shell', () => {
  const setup = (mobile = false, superAdmin = false) => {
    const matches = new BehaviorSubject({ matches: mobile, breakpoints: {} });
    const auth = {
      logout: vi.fn(), tryRestoreSession: () => of(false),
      isSuperAdmin: () => superAdmin, homeRoute: () => (superAdmin ? '/admin/workspaces' : '/leads')
    };
    const workspace = { current: vi.fn(() => of({ id: 'w1', name: 'Acme Inc' })) };
    TestBed.configureTestingModule({
      imports: [AppModule],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: OpsService, useValue: { status: () => of({ canOpenDashboard: false }), openDashboard: () => of(undefined) } },
        { provide: WorkspaceService, useValue: workspace },
        { provide: BreakpointObserver, useValue: { observe: () => matches } }
      ]
    });
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    return { fixture, auth, workspace };
  };

  it('renders the four nav items', async () => {
    const { fixture } = setup();
    await fixture.whenStable();
    const labels = Array.from<Element>(fixture.nativeElement.querySelectorAll('mat-nav-list a')).map(a => a.textContent);
    expect(labels).toHaveLength(4);
    ['Leads', 'Campaigns', 'Inbox', 'Analytics'].forEach((l, i) => expect(labels[i]).toContain(l));
  });

  it('shows the workspace name once it resolves', async () => {
    const { fixture } = setup();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('.workspace-name').textContent).toContain('Acme Inc');
  });

  it('shows the super admin only the Workspaces item and never asks for a workspace', async () => {
    const { fixture, workspace } = setup(false, true);
    await fixture.whenStable();
    fixture.detectChanges();

    const links = Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('mat-nav-list a'));
    expect(links).toHaveLength(1);
    expect(links[0].textContent).toContain('Workspaces');
    expect(links[0].getAttribute('href')).toBe('/admin/workspaces');
    expect(workspace.current).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.workspace-name').textContent).toContain('Platform admin');
  });

  it('logout clears the session and navigates to /login', () => {
    const { fixture, auth } = setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.componentInstance.logout();
    expect(auth.logout).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('uses an overlay sidenav on a narrow viewport', async () => {
    const { fixture } = setup(true);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.componentInstance.isMobile).toBe(true);
    expect(fixture.nativeElement.querySelector('mat-sidenav').classList).toContain('mat-drawer-over');
  });
});
