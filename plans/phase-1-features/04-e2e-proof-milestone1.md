# Feature 4 — Milestone 1 End-to-End Proof

**Branch:** `feature/e2e-proof-milestone1`
**Milestone:** 1 — Scaffolding
**Depends on:** Features 1–3 merged.

## Goal
A real Leads screen exists (so the manual e2e pass has something to click through), and the risky auth logic (duplicate email, JWT claim contents, refresh rotation/expiry/revocation) has unit test coverage.

## Files to add — Angular Leads screen

### `ZenLead.Client/src/app/features/leads/leads.models.ts` (new)
```typescript
export type LeadStatus = 'New' | 'Contacted' | 'Replied' | 'Unsubscribed';

export interface Lead {
  id: string;
  name: string;
  email: string;
  title: string | null;
  status: LeadStatus;
  createdAt: string;
}

export interface CreateLeadRequest {
  name: string;
  email: string;
  title: string | null;
}
```

### `ZenLead.Client/src/app/features/leads/leads.service.ts` (new)
```typescript
import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CreateLeadRequest, Lead } from './leads.models';

@Injectable({ providedIn: 'root' })
export class LeadsService {
  constructor(private http: HttpClient) {}

  list(): Observable<Lead[]> {
    return this.http.get<Lead[]>('/api/v1/leads');
  }

  create(request: CreateLeadRequest): Observable<Lead> {
    return this.http.post<Lead>('/api/v1/leads', request);
  }
}
```

### `ZenLead.Client/src/app/features/leads/leads-list/leads-list.ts` (new)
```typescript
import { Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Lead } from '../leads.models';
import { LeadsService } from '../leads.service';

@Component({
  selector: 'app-leads-list',
  standalone: false,
  templateUrl: './leads-list.html',
  styleUrl: './leads-list.css'
})
export class LeadsList implements OnInit {
  leads: Lead[] = [];
  loading = true;
  errorMessage: string | null = null;

  form: FormGroup = this.fb.group({
    name: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    title: ['']
  });
  submitting = false;

  constructor(private fb: FormBuilder, private leadsService: LeadsService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading = true;
    this.leadsService.list().subscribe({
      next: leads => { this.leads = leads; this.loading = false; },
      error: () => { this.errorMessage = 'Failed to load leads.'; this.loading = false; }
    });
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;

    this.leadsService.create(this.form.getRawValue()).subscribe({
      next: () => {
        this.submitting = false;
        this.form.reset();
        this.refresh();
      },
      error: () => {
        this.submitting = false;
        this.errorMessage = 'Failed to create lead.';
      }
    });
  }
}
```

### `ZenLead.Client/src/app/features/leads/leads-list/leads-list.html` (new)
```html
<h1>Leads</h1>

<form [formGroup]="form" (ngSubmit)="submit()" class="add-lead-form">
  <mat-form-field appearance="outline">
    <mat-label>Name</mat-label>
    <input matInput formControlName="name">
  </mat-form-field>
  <mat-form-field appearance="outline">
    <mat-label>Email</mat-label>
    <input matInput type="email" formControlName="email">
  </mat-form-field>
  <mat-form-field appearance="outline">
    <mat-label>Title</mat-label>
    <input matInput formControlName="title">
  </mat-form-field>
  <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || submitting">
    Add lead
  </button>
</form>

<p *ngIf="errorMessage" class="leads-error">{{ errorMessage }}</p>
<p *ngIf="loading">Loading…</p>

<table mat-table [dataSource]="leads" *ngIf="!loading">
  <ng-container matColumnDef="name">
    <th mat-header-cell *matHeaderCellDef>Name</th>
    <td mat-cell *matCellDef="let lead">{{ lead.name }}</td>
  </ng-container>
  <ng-container matColumnDef="email">
    <th mat-header-cell *matHeaderCellDef>Email</th>
    <td mat-cell *matCellDef="let lead">{{ lead.email }}</td>
  </ng-container>
  <ng-container matColumnDef="status">
    <th mat-header-cell *matHeaderCellDef>Status</th>
    <td mat-cell *matCellDef="let lead">{{ lead.status }}</td>
  </ng-container>

  <tr mat-header-row *matHeaderRowDef="['name', 'email', 'status']"></tr>
  <tr mat-row *matRowDef="let row; columns: ['name', 'email', 'status']"></tr>
</table>
```
(`leads-list.css` — trivial: `.add-lead-form { display: flex; gap: 12px; align-items: baseline; } .leads-error { color: #b00020; }`)

### `ZenLead.Client/src/app/app-module.ts` (modified — add the new declarations/imports)
```typescript
// add to existing imports:
import { MatTableModule } from '@angular/material/table';
import { LeadsList } from './features/leads/leads-list/leads-list';

// declarations: [..., LeadsList]
// imports: [..., MatTableModule]
```

### `ZenLead.Client/src/app/app-routing-module.ts` (modified — add the guarded route deferred from Feature 3)
```typescript
import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { LeadsList } from './features/leads/leads-list/leads-list';
import { authGuard } from './core/auth/auth.guard';

const routes: Routes = [
  { path: '', redirectTo: 'login', pathMatch: 'full' },
  { path: 'register', component: Register },
  { path: 'login', component: Login },
  { path: 'leads', component: LeadsList, canActivate: [authGuard] }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
```

## Files to add — backend unit tests

### `ZenLead.Tests/ZenLead.Tests.csproj` (modified)
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.12" />
```
Needed to test `RefreshTokenService`'s rotation logic against a real `ZenLeadDbContext` without a SQL Server dependency in CI. No mocking library added — hand-written fakes are used for the Application-layer tests below, consistent with the project's existing "fake `IEmailComposer`" testing convention (see [phase-1-poc-implementation-plan.md](../phase-1-poc-implementation-plan.md) testing conventions).

### `ZenLead.Tests/Application/Auth/FakeAuthAbstractions.cs` (new — shared hand-written fakes)
```csharp
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Tests.Application.Auth;

public class FakeIdentityService : IIdentityService
{
    public List<string> RegisteredEmails { get; } = [];
    public Guid NextUserId { get; set; } = Guid.NewGuid();
    public Guid WorkspaceIdForUser { get; set; }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => Task.FromResult(RegisteredEmails.Contains(email));

    public Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default)
    {
        RegisteredEmails.Add(email);
        WorkspaceIdForUser = workspaceId;
        return Task.FromResult(NextUserId);
    }

    public Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
        => Task.FromResult<Guid?>(RegisteredEmails.Contains(email) ? NextUserId : null);

    public Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<(Guid, string)?>((WorkspaceIdForUser, "fake@example.com"));
}

public class FakeWorkspaceRepository : IWorkspaceRepository
{
    public Task<Workspace> CreateAsync(string name, CancellationToken ct = default)
        => Task.FromResult(new Workspace { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow });
}

public class FakeJwtTokenGenerator : IJwtTokenGenerator
{
    public string GenerateAccessToken(Guid userId, Guid workspaceId, string email)
        => $"fake-token:{userId}:{workspaceId}:{email}";
}

public class FakeRefreshTokenService : IRefreshTokenService
{
    public Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult("fake-refresh-token");

    public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
        => Task.FromResult(new RefreshResult(true, Guid.NewGuid(), "new-fake-refresh-token", null));
}
```

### `ZenLead.Tests/Application/Auth/RegisterWorkspaceUseCaseTests.cs` (new)
```csharp
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RegisterWorkspaceUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesWorkspaceAndReturnsTokens()
    {
        var sut = new RegisterWorkspaceUseCase(
            new FakeWorkspaceRepository(), new FakeIdentityService(),
            new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123", "Alice"));

        Assert.NotNull(result.AccessToken);
        Assert.Equal("fake-refresh-token", result.RefreshToken);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateEmail_Throws()
    {
        var identity = new FakeIdentityService { RegisteredEmails = { "a@acme.com" } };
        var sut = new RegisterWorkspaceUseCase(
            new FakeWorkspaceRepository(), identity,
            new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123", "Alice")));
    }
}
```

### `ZenLead.Tests/Application/Auth/LoginUseCaseTests.cs` (new)
```csharp
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class LoginUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ValidCredentials_ReturnsTokens()
    {
        var identity = new FakeIdentityService { RegisteredEmails = { "a@acme.com" } };
        var sut = new LoginUseCase(identity, new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new LoginRequest("a@acme.com", "whatever"));

        Assert.NotNull(result);
        Assert.Equal("fake-refresh-token", result!.RefreshToken);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownEmail_ReturnsNull()
    {
        var sut = new LoginUseCase(new FakeIdentityService(), new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new LoginRequest("nobody@acme.com", "whatever"));

        Assert.Null(result);
    }
}
```
`FakeIdentityService.ValidateCredentialsAsync` doesn't actually check the password — it only cares whether the email is "registered" — so this test covers the use case's own branching (known vs. unknown user), not password verification; that lives in `IdentityService`/`UserManager`, backed by real Identity machinery, not this fake.

### `ZenLead.Tests/Infrastructure/Identity/JwtTokenGeneratorTests.cs` (new)
```csharp
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using ZenLead.Infrastructure.Identity;

namespace ZenLead.Tests.Infrastructure.Identity;

public class JwtTokenGeneratorTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-at-least-32-chars-long!!",
            ["Jwt:Issuer"] = "ZenLead",
            ["Jwt:Audience"] = "ZenLeadClient"
        })
        .Build();

    [Fact]
    public void GenerateAccessToken_IncludesExpectedClaims()
    {
        var sut = new JwtTokenGenerator(BuildConfig());
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var token = sut.GenerateAccessToken(userId, workspaceId, "a@acme.com");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(userId.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(workspaceId.ToString(), jwt.Claims.First(c => c.Type == "workspace_id").Value);
        Assert.Equal("a@acme.com", jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal("ZenLead", jwt.Issuer);
    }
}
```

### `ZenLead.Tests/Infrastructure/Identity/RefreshTokenServiceTests.cs` (new)
```csharp
using Microsoft.EntityFrameworkCore;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Infrastructure.Identity;

public class RefreshTokenServiceTests
{
    private static ZenLeadDbContext NewDb() => new(
        new DbContextOptionsBuilder<ZenLeadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ValidateAndRotateAsync_ValidToken_RotatesAndRevokesOld()
    {
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var userId = Guid.NewGuid();
        var raw = await sut.IssueAsync(userId);

        var result = await sut.ValidateAndRotateAsync(raw);

        Assert.True(result.Succeeded);
        Assert.Equal(userId, result.UserId);
        Assert.NotEqual(raw, result.NewRawToken);

        var reuse = await sut.ValidateAndRotateAsync(raw);
        Assert.False(reuse.Succeeded); // old token now revoked
    }

    [Fact]
    public async Task ValidateAndRotateAsync_UnknownToken_Fails()
    {
        using var db = NewDb();
        var sut = new RefreshTokenService(db);

        var result = await sut.ValidateAndRotateAsync("never-issued");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidateAndRotateAsync_ExpiredToken_Fails()
    {
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var userId = Guid.NewGuid();
        var raw = await sut.IssueAsync(userId);

        var token = await db.RefreshTokens.FirstAsync();
        token.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        var result = await sut.ValidateAndRotateAsync(raw);

        Assert.False(result.Succeeded);
    }
}
```

## Manual e2e smoke pass (no code — a checklist to run by hand)
1. Register via UI → redirected to `/login`.
2. Log in → redirected to `/leads`.
3. Add a lead via the inline form → it appears in the table.
4. Reload the browser tab → `APP_INITIALIZER` silently calls `/auth/refresh` → still on `/leads`, not bounced to `/login`.
5. `GET /api/v1/leads` (via the UI reload, or Swagger with the same bearer token) still shows the lead.

## Verification
- `dotnet test ZenLead.Tests` — all new tests pass.
- `ng build` succeeds.
- Manual pass above completed once, end to end, against a real LocalDB.
