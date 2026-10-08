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
import { WorkspaceService } from '../workspace.service';

describe('Shell', () => {
  const setup = (mobile = false) => {
    const matches = new BehaviorSubject({ matches: mobile, breakpoints: {} });
    const auth = { logout: vi.fn(), tryRestoreSession: () => of(false) };
    TestBed.configureTestingModule({
      imports: [AppModule],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: WorkspaceService, useValue: { current: () => of({ id: 'w1', name: 'Acme Inc' }) } },
        { provide: BreakpointObserver, useValue: { observe: () => matches } }
      ]
    });
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    return { fixture, auth };
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
