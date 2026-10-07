# Feature 12 — App Shell

**Branch:** `feature/angular-shell`
**Sprint:** 1
**Depends on:** F11 only in that the API response shapes changed; the shell itself is independent and can start in parallel with F11.

## Goal
Replace the Phase 1 single `<header>` toolbar with a Material sidenav + toolbar shell (nav to Leads / Campaigns / Inbox / Analytics, workspace name, logout) and move the app to **lazy-loaded feature modules**, so every later feature adds routes in its own folder instead of growing `app-module.ts`. Campaigns/Inbox/Analytics are stubs until Sprints 3–5. No role-aware UI (F19 adds the one role switch).

## Files to add/modify

### Backend (tiny — needed for the workspace name in the toolbar)

**`ZenLead.Application/Dtos/Workspaces/WorkspaceDtos.cs`** (new)
```csharp
namespace ZenLead.Application.Dtos.Workspaces;

public record CurrentWorkspaceResponse(Guid Id, string Name);
```

**`ZenLead.Application/Abstractions/IWorkspaceRepository.cs`** (modified) — add
```csharp
Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
```
**`ZenLead.Infrastructure/Persistence/WorkspaceRepository.cs`** — `=> db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct)`. Update `FakeWorkspaceRepository` in `ZenLead.Tests/Application/Auth/FakeAuthAbstractions.cs` (return from `Created`).

**`ZenLead.Api/Controllers/V1/WorkspacesController.cs`** (new)
```csharp
[ApiController]
[Authorize]
[Route("api/v1/workspaces")]
public class WorkspacesController(IWorkspaceRepository workspaces) : ControllerBase
{
    [HttpGet("current")]
    public async Task<ActionResult<CurrentWorkspaceResponse>> Current(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var workspace = await workspaces.GetByIdAsync(workspaceId, ct);
        return workspace is null ? NotFound() : Ok(new CurrentWorkspaceResponse(workspace.Id, workspace.Name));
    }
}
```
F20 adds an anonymous `[HttpGet]` (workspace picker) to this same controller; keep `current` literal-route and `[Authorize]` on the **action**, not the class, once F20 lands (F20 doc says so).

### Angular (`ZenLead.Client/src`)

**`index.html`** — add the icon font (Material nav icons): `<link href="https://fonts.googleapis.com/icon?family=Material+Icons" rel="stylesheet">`. (F29's CSP allow-lists `fonts.googleapis.com`/`fonts.gstatic.com`; or self-host via `@fontsource/material-icons` then — noted there.)

**`app/shared/shared-module.ts`** (new) — the one place Material is imported.
```ts
import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';

const MATERIAL = [
  MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, MatTableModule,
  MatProgressSpinnerModule, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatMenuModule
];

@NgModule({
  imports: [CommonModule, ReactiveFormsModule, RouterModule, ...MATERIAL],
  exports: [CommonModule, ReactiveFormsModule, RouterModule, ...MATERIAL]
})
export class SharedModule {}
```
Later features append to `MATERIAL` (paginator, sort, select, chips, stepper, dialog, snackbar, tabs, progress-bar…) — one line each, in the feature that needs it.

**`app/core/layout/shell/shell.ts`, `shell.html`, `shell.css`** (new, declared in `CoreModule`/`app-module`)
```ts
@Component({ selector: 'app-shell', standalone: false, templateUrl: './shell.html', styleUrl: './shell.css' })
export class Shell implements OnInit {
  workspaceName: string | null = null;
  isMobile = false;

  readonly navItems = [
    { label: 'Leads', icon: 'people', link: '/leads' },
    { label: 'Campaigns', icon: 'campaign', link: '/campaigns' },
    { label: 'Inbox', icon: 'inbox', link: '/inbox' },
    { label: 'Analytics', icon: 'insights', link: '/analytics' }
  ];

  constructor(private auth: AuthService, private router: Router, private workspace: WorkspaceService,
              private breakpoints: BreakpointObserver, private cdr: ChangeDetectorRef) {}

  ngOnInit(): void {
    this.workspace.current().subscribe({ next: w => { this.workspaceName = w.name; this.cdr.markForCheck(); }, error: () => {} });
    this.breakpoints.observe('(max-width: 800px)').subscribe(r => { this.isMobile = r.matches; this.cdr.markForCheck(); });
  }

  logout(): void { this.auth.logout(); this.router.navigate(['/login']); }
}
```
```html
<mat-toolbar color="primary" class="shell-toolbar">
  @if (isMobile) { <button mat-icon-button (click)="nav.toggle()" aria-label="Toggle navigation"><mat-icon>menu</mat-icon></button> }
  <a class="app-name" routerLink="/leads">ZenLead</a>
  <span class="spacer"></span>
  <span class="workspace-name">{{ workspaceName }}</span>
  <button mat-icon-button [matMenuTriggerFor]="account" aria-label="Account"><mat-icon>account_circle</mat-icon></button>
  <mat-menu #account="matMenu"><button mat-menu-item (click)="logout()">Log out</button></mat-menu>
</mat-toolbar>

<mat-sidenav-container class="shell-container">
  <mat-sidenav #nav [mode]="isMobile ? 'over' : 'side'" [opened]="!isMobile">
    <mat-nav-list>
      @for (item of navItems; track item.link) {
        <a mat-list-item [routerLink]="item.link" routerLinkActive="active" (click)="isMobile && nav.close()">
          <mat-icon matListItemIcon>{{ item.icon }}</mat-icon>
          <span matListItemTitle>{{ item.label }}</span>
        </a>
      }
    </mat-nav-list>
  </mat-sidenav>
  <mat-sidenav-content><router-outlet></router-outlet></mat-sidenav-content>
</mat-sidenav-container>
```
CSS: `.shell-container { height: calc(100vh - 64px); }`, `.spacer { flex: 1 }`, `mat-sidenav { width: 220px }`, `.active { background: var(--mat-sys-secondary-container) }`, `mat-sidenav-content { padding: 16px 24px }`. Needs `@angular/cdk/layout` (already a transitive dependency of Material).

**`app/core/layout/workspace.service.ts`** (new)
```ts
@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  constructor(private http: HttpClient) {}
  current(): Observable<{ id: string; name: string }> { return this.http.get<{ id: string; name: string }>('/api/v1/workspaces/current'); }
}
```

**`app/core/placeholder/coming-soon.ts`** (new, declared once) — tiny component: `<h1>{{ title }}</h1><p>Coming in Sprint {{ sprint }}.</p>` reading `route.snapshot.data`.

**`app/app-routing-module.ts`** (modified)
```ts
const routes: Routes = [
  { path: '', redirectTo: 'leads', pathMatch: 'full' },
  { path: 'login', component: Login },
  { path: 'register', component: Register },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: 'leads', loadChildren: () => import('./features/leads/leads-module').then(m => m.LeadsModule) },
      { path: 'campaigns', component: ComingSoon, data: { title: 'Campaigns', sprint: 3 } },
      { path: 'inbox', component: ComingSoon, data: { title: 'Inbox', sprint: 4 } },
      { path: 'analytics', component: ComingSoon, data: { title: 'Analytics', sprint: 5 } }
    ]
  },
  { path: '**', redirectTo: 'leads' }
];
```
`path: ''` → `leads` while logged out is bounced to `/login` by `authGuard` (existing behaviour). Later features swap the stub child for `loadChildren` (F24 campaigns, F27 inbox, F28 analytics).

**`app/features/leads/leads-module.ts`, `leads-routing-module.ts`** (new) — move `LeadsList`/`LeadDetail` declarations out of `app-module.ts`:
```ts
const routes: Routes = [
  { path: '', component: LeadsList },
  { path: ':id', component: LeadDetail }
];
@NgModule({
  declarations: [LeadsList, LeadDetail],
  imports: [SharedModule, RouterModule.forChild(routes)]
})
export class LeadsModule {}
```
(`/leads/import` and `/leads/profiles` — F16/F14 — must be declared **before** `:id` in this array when they land.)

**`app/app-module.ts`** (modified) — remove `LeadsList`, `LeadDetail` and the Material imports; import `SharedModule`; declare `Shell`, `ComingSoon`; everything else (interceptor, app initializer) unchanged.

**`app/app.html`** → just `<router-outlet></router-outlet>`; **`app/app.ts`** — drop `logout()` (moved to the shell) and the constructor params; **`app/app.css`** — remove the toolbar styles that moved into `shell.css`.

**`app/app.spec.ts`**, **`leads-list.spec.ts`**, **`lead-detail.spec.ts`** — adjust `TestBed` setup: import `SharedModule` (or the needed Material modules) instead of relying on `AppModule`; `app.spec.ts` no longer expects a toolbar.

## Tests
- `shell.spec.ts`: renders four nav items; clicking *Log out* calls `AuthService.logout()` and navigates to `/login`; workspace name shows after `WorkspaceService.current()` resolves; on a narrow viewport (mock `BreakpointObserver`) sidenav mode is `over`.
- Backend: `WorkspacesControllerTests` — `Current` returns the caller's workspace; missing claim → 401. (A "different workspace" case is not applicable: the id comes from the claim.)
- Existing `auth.guard.spec.ts` / `auth.interceptor.spec.ts` unchanged.

## Not in this feature
Role-aware navigation (F19 hides tenant nav for the super admin), real Campaigns/Inbox/Analytics screens, theming changes, breadcrumbs.

## Verification
`ng build` (lazy chunk for leads appears in the output), `ng test`, then manually: log in → shell with four nav items, workspace name top-right; deep link `/leads/<id>` while logged out → `/login`; resize below 800 px → hamburger menu; Campaigns/Inbox/Analytics show the placeholder; refresh on `/leads` stays logged in (session restore unchanged).
