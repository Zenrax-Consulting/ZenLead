# Feature 30 — Azure Provisioning & CI/CD

**Branch:** `feature/azure-cicd`
**Sprint:** 5 — **deliberately the last feature** before UAT. Everything before it ran on the developer machine, so this feature must also prove, for the first time, every production-only code path: Key Vault + managed identity, Azure Blob, Azure SQL with Hangfire, Application Insights, the real domain, and the production webhook URLs.
**Depends on:** F29 (security headers, forwarded headers, logging rules, runbooks, perf baseline), F17 (DNS/SendGrid), everything else merged. **Budget a buffer for surprises** (parent risk table): cut F29 polish and charts before cutting this.

## Findings from the repo that shape this feature
1. **`dotnet publish` does not currently produce the single artifact.** `ZenLead.Client.esproj` has `ShouldRunBuildScript=false`, `ZenLead.Api.csproj` has no step that copies the Angular production build into `wwwroot`, and `ZenLead.Api/wwwroot` doesn't exist. `CLAUDE.md` (and the parent plan) describe this as working — it needs to be made true here (PBI 30.3-a).
2. **`.github/workflows/` exists but is empty** — there is no CI at all yet.
3. **`EfUnitOfWork` opens transactions with `BeginTransactionAsync` directly.** That throws at runtime as soon as the SQL Server retry strategy (needed for Azure SQL serverless wake-ups and transient faults) is enabled. It must run inside `CreateExecutionStrategy()` (PBI 30.4-b). The same applies to any other manual transaction added in Phase 2.
4. Static assets are served with `MapStaticAssets()` (build-time manifest), so the Angular build must be in `ZenLead.Api/wwwroot` **before** `dotnet publish`, not appended to the publish folder afterwards.

## Design decisions
- **Infrastructure as code in `infra/` (Bicep)**, one resource group, deployable repeatedly. Names derive from one `appName` parameter.
- **No secrets in app settings or CI.** The App Service reads secrets from Key Vault via managed identity; SQL and Blob use Entra ID auth (no SQL password, no storage keys; `allowSharedKeyAccess = false`). GitHub Actions authenticates to Azure with **OIDC federated credentials** — the only values stored in GitHub are non-secret IDs.
- **Single instance, no staging slot** (B1). A failed smoke check fails the pipeline; rollback = redeploy the previous commit (`workflow_dispatch` with a ref). Migrations are **forward-only** (F29 runbook).
- **Migrations are applied by the pipeline** (`dotnet ef migrations bundle`), never at app start in production.
- **SQL: free serverless offer first, Basic as fallback**, switchable by one Bicep parameter, because Hangfire polling may defeat auto-pause (parent §1a).

## Files to add/modify

### 30.1 — Infrastructure (`infra/`)

```
infra/
  main.bicep                 # resource-group scope; wires the modules
  main.bicepparam            # values for the prod environment (no secrets)
  modules/
    monitoring.bicep         # Log Analytics (daily cap) + Application Insights
    keyvault.bicep           # Key Vault (Standard, RBAC, purge protection)
    storage.bicep            # Storage account + private container + 30-day lifecycle rule
    sql.bicep                # SQL server (Entra-only) + database (free offer | Basic)
    webapp.bicep             # B1 Linux plan + web app + identity + app settings + health check
    roles.bicep              # role assignments for the web app identity
    budget.bicep             # Cost Management budget + alert contacts
    alerts.bicep             # App Insights alert rules (30.7)
  sql/grant-app-identity.sql # contained user for the web app's managed identity
  scripts/deploy.ps1         # az deployment group create … (what-if first)
  scripts/set-secrets.ps1    # prompts for each secret, writes to Key Vault; never echoes or stores values
  README.md                  # runbook (below)
```
**`main.bicep`** (shape; **verify every API version/property against current Azure docs when writing it** — especially the SQL free-offer properties)
```bicep
targetScope = 'resourceGroup'

@description('Short unique name, lowercase letters/digits, e.g. zenlead')
param appName string
param location string = resourceGroup().location
@description('Entra object id + login name of the person/group who administers SQL (also the CI identity gets access separately)')
param sqlAdminObjectId string
param sqlAdminLogin string
param useSqlFreeOffer bool = true
param budgetAmountUsd int = 40
param alertEmail string
param publicHostName string          // leads.zenraxconsultancy.com

module monitoring 'modules/monitoring.bicep' = { name: 'monitoring', params: { appName: appName, location: location, dailyCapGb: 1 } }
module kv 'modules/keyvault.bicep' = { name: 'kv', params: { appName: appName, location: location } }
module storage 'modules/storage.bicep' = { name: 'storage', params: { appName: appName, location: location } }
module sql 'modules/sql.bicep' = { name: 'sql', params: { appName: appName, location: location, adminObjectId: sqlAdminObjectId, adminLogin: sqlAdminLogin, useFreeOffer: useSqlFreeOffer } }
module web 'modules/webapp.bicep' = {
  name: 'web'
  params: {
    appName: appName, location: location, keyVaultUri: kv.outputs.vaultUri, storageAccountUri: storage.outputs.blobUri,
    appInsightsConnectionString: monitoring.outputs.connectionString, publicHostName: publicHostName
  }
}
module roles 'modules/roles.bicep' = { name: 'roles', params: { principalId: web.outputs.principalId, keyVaultName: kv.outputs.name, storageName: storage.outputs.name } }
module budget 'modules/budget.bicep' = { name: 'budget', params: { amountUsd: budgetAmountUsd, alertEmail: alertEmail } }
module alerts 'modules/alerts.bicep' = { name: 'alerts', params: { location: location, appInsightsId: monitoring.outputs.appInsightsId, logAnalyticsId: monitoring.outputs.workspaceId, alertEmail: alertEmail, webAppId: web.outputs.id } }
```
Key points per module:
- **`monitoring.bicep`** — `Microsoft.OperationalInsights/workspaces` (`PerGB2018`, `workspaceCapping.dailyQuotaGb`), workspace-based `Microsoft.Insights/components`; outputs the connection string.
- **`keyvault.bicep`** — `Microsoft.KeyVault/vaults` Standard, `enableRbacAuthorization: true`, `enableSoftDelete: true`, `enablePurgeProtection: true`, `publicNetworkAccess: 'Enabled'` (no private endpoint at this scale; firewall defaults documented as an accepted risk in `docs/security-review.md`).
- **`storage.bicep`** — `StorageV2`, `Standard_LRS`, `accessTier: 'Hot'`, `allowBlobPublicAccess: false`, `allowSharedKeyAccess: false`, `minimumTlsVersion: 'TLS1_2'`, `supportsHttpsTrafficOnly: true`; blob service + container `zenlead` (private); **management policy** with one rule: `baseBlob.delete.daysAfterModificationGreaterThan: 30` filtered to `blobTypes: ['blockBlob']`, `prefixMatch: ['zenlead/imports/']`.
- **`sql.bicep`** — `Microsoft.Sql/servers` with `administrators { administratorType: 'ActiveDirectory', azureADOnlyAuthentication: true, … }`, `minimalTlsVersion: '1.2'`; firewall rule "AllowAzureServices" (`0.0.0.0`) — accepted trade-off vs VNet integration costs (documented); database `zenlead`: free offer (`useFreeLimit: true`, `freeLimitExhaustionBehavior: 'AutoPause'`, serverless GP_S_Gen5_1, `autoPauseDelay` 60) **or** `Basic` (`sku: { name: 'Basic', tier: 'Basic' }`) per `useFreeOffer`; `requestedBackupStorageRedundancy: 'Local'`.
- **`webapp.bicep`** — plan `B1` Linux (`reserved: true`); site `kind: 'app,linux'`, `identity: SystemAssigned`, `httpsOnly: true`, `siteConfig`: `linuxFxVersion: 'DOTNETCORE|10.0'`, `alwaysOn: true`, `minTlsVersion: '1.2'`, `ftpsState: 'Disabled'`, `http20Enabled: true`, `healthCheckPath: '/health'`, `appSettings`: `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `KeyVault__Uri`, `Storage__AccountUri`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `App__PublicBaseUrl=https://<publicHostName>`, `AllowedHosts=<publicHostName>`, `Email__Provider=SendGrid`, `Email__DomainVerified=true` (set after F17 verification), `Sending__Enabled=true`, `Hangfire__Enabled=true`, `WEBSITE_RUN_FROM_PACKAGE=1`. **No secrets here.** (Free managed certificate + hostname binding are CLI steps, see 30.6, because they need DNS to exist first.)
- **`roles.bicep`** — `Key Vault Secrets User` (`4633458b-17de-4289-b4b2-b52cf8ab0bf6`) on the vault and `Storage Blob Data Contributor` (`ba92f5b4-2d11-453d-a403-e96b0029c9fe`) on the storage account, both for the web app's principal.
- **`budget.bicep`** — `Microsoft.Consumption/budgets` monthly, amount `budgetAmountUsd`, notifications at 50 % / 80 % actual and 100 % forecasted to `alertEmail`.
- **`sql/grant-app-identity.sql`** — run once as the SQL Entra admin (`sqlcmd -G -S <server> -d zenlead`):
```sql
CREATE USER [<appName>] FROM EXTERNAL PROVIDER;           -- the web app's managed identity
ALTER ROLE db_datareader ADD MEMBER [<appName>];
ALTER ROLE db_datawriter ADD MEMBER [<appName>];
ALTER ROLE db_ddladmin  ADD MEMBER [<appName>];          -- Hangfire creates its [HangFire] schema at first start (PrepareSchemaIfNecessary)
-- the CI identity (federated service principal) needs db_owner for migrations; create it the same way with its display name
```
**`infra/README.md`** — ordered runbook: `az group create`; `scripts/deploy.ps1 -WhatIf` then real; run `grant-app-identity.sql`; `scripts/set-secrets.ps1`; DNS + domain (30.6); GitHub OIDC setup; first pipeline run. Includes the **cost table** from parent §1a with the "verify prices" caveat and the first-upgrades order.

### 30.2 — Configuration & secrets

**Packages:** `ZenLead.Api` → `Azure.Extensions.AspNetCore.Configuration.Secrets`, `Azure.Identity`.

**`Program.cs`** (modified; *first thing after `CreateBuilder`, before `StartupConfiguration.ThrowIfInvalid`*)
```csharp
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri)
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
// DefaultAzureCredential: managed identity in Azure, `az login`/Visual Studio locally — so the same code path can be exercised from a dev box against a dev vault
```
Secret names use `--` for `:` (`Jwt--SigningKey`). Store these in the vault via `set-secrets.ps1` (list is the single source of truth, kept in `infra/README.md`):
`Jwt--SigningKey`, `ConnectionStrings--Default`, `OpenAI--ApiKey`, `SendGrid--ApiKey`, `SendGrid--InboundSecret`, `SendGrid--EventWebhook--PublicKey`, `Unsubscribe--SigningKey`, `SuperAdmin--Email`, `SuperAdmin--Password`, `Hangfire--AdminEmails--0`, `LeadSource--ApiKey`. Non-secret operational settings stay in app settings.

**`ConnectionStrings--Default`** has **no password** (Entra auth): `Server=tcp:<server>.database.windows.net,1433;Database=zenlead;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;ConnectRetryCount=5;` (serverless wake-up can take ~1 minute; the long timeout plus retries absorb it). It lives in the vault anyway to match the parent plan and keep one config path.

**`StartupConfiguration.cs`** (modified) — production rules, still failing fast with the same helpful messages:
- `Production`: require `KeyVault:Uri` **or** every individual secret key (so a misconfigured deploy can't half-start); require `Sending:Mode` to be set **explicitly** (`Restricted` or `Live`; absence is an error so nobody goes live by omission), `App:PublicBaseUrl` (https), `Storage:AccountUri` (or connection string), `Email:Provider = SendGrid`, `SendGrid:ApiKey|InboundSecret|EventWebhook:PublicKey`, `Unsubscribe:SigningKey` (≥ 32 bytes), `LeadSource:Provider != Fake` + key, `Hangfire:AdminEmails` non-empty, `AllowedHosts != "*"`, and either `SuperAdmin:*` or an already-seeded super admin (F19 rule).
- Log **names** of missing keys, never values. Unit tests in `StartupAndClaimsTests` for each rule.
- `Jwt:SigningKey` rotation: generating a new key signs everyone out (acceptable) — **not** `Unsubscribe:SigningKey` (old email links would die); noted in the runbook.

### 30.3 — Make `dotnet publish` produce the single artifact, then CI/CD

**a) Client build into `wwwroot` (explicit and verifiable)**
- Add **`scripts/build-client.ps1`** and **`scripts/publish.ps1`**: `cd ZenLead.Client; npm ci; npm run build -- --configuration production`, then **mirror** `dist/ZenLead.Client/browser/` into `ZenLead.Api/wwwroot/` (delete stale files first), then `dotnet publish ZenLead.Api -c Release -o publish`. Add `ZenLead.Api/wwwroot/` to `.gitignore`. CI uses the same two steps (below), so local and CI produce the same layout.
- **Verify:** the published output contains `wwwroot/index.html` and fingerprinted assets, `MapStaticAssets()` serves them (it reads a **build-time** manifest — the Angular files must be in `wwwroot` *before* `dotnet publish`), a deep link such as `/leads/123` falls back to `index.html`, and `/api/...` still hits controllers. If `MapStaticAssets` ignores the copied files for any reason, add `app.UseStaticFiles()` ahead of it — decide from the test, not by assumption.
- Update `CLAUDE.md`'s "one deployable artifact" paragraph and the README publish instructions to describe this actual mechanism.

**b) `.github/workflows/ci.yml`** (pull requests and pushes to non-main branches)
```yaml
name: ci
on:
  pull_request:
  push: { branches-ignore: [main] }

jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: '22', cache: npm, cache-dependency-path: ZenLead.Client/package-lock.json }   # use the Node version Angular 21 requires (check package.json "engines"/CLI docs)
      - name: Backend build + tests
        run: |
          dotnet restore ZenLead.slnx
          dotnet build ZenLead.slnx -c Release --no-restore -p:SkipClientBuild=true
          dotnet test ZenLead.Tests -c Release --no-build --filter "Category!=Perf&Category!=Azurite&Category!=LiveAi"
      - name: Frontend install + tests
        working-directory: ZenLead.Client
        run: |
          npm ci
          npx ng test --watch=false          # Vitest runner; verify the headless flag set for the installed Angular version
          npx ng build --configuration production
      - name: Dependency audit
        run: |
          dotnet list ZenLead.slnx package --vulnerable --include-transitive | tee vuln.txt
          if grep -qE "(High|Critical)" vuln.txt; then echo "::error::High/Critical vulnerable package"; exit 1; fi
          cd ZenLead.Client && npm audit --omit=dev --audit-level=high
```
> The `ZenLead.Api` project references the Angular `.esproj`; on a build agent that reference should not run an `npm` build during the `dotnet build` step. **Verify** how the JavaScript SDK behaves on Linux (`ShouldRunBuildScript=false` already prevents the build script) and whether the reference needs a `Condition` for CI.

**c) `.github/workflows/deploy.yml`** (push to `main`, and `workflow_dispatch` with an optional `ref` for rollback)
```yaml
name: deploy
on:
  push: { branches: [main] }
  workflow_dispatch:
    inputs: { ref: { description: 'Git ref to deploy (rollback)', required: false } }

permissions: { id-token: write, contents: read }       # OIDC to Azure; no stored secrets
concurrency: { group: deploy-prod, cancel-in-progress: false }

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4
        with: { ref: '${{ inputs.ref || github.sha }}' }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: '22', cache: npm, cache-dependency-path: ZenLead.Client/package-lock.json }

      - name: Test (same gate as PRs)
        run: |
          dotnet test ZenLead.slnx -c Release --filter "Category!=Perf&Category!=Azurite&Category!=LiveAi" -p:SkipClientBuild=true
          cd ZenLead.Client && npm ci && npx ng test --watch=false

      - name: Build client into wwwroot
        run: |
          cd ZenLead.Client && npm run build -- --configuration production
          rm -rf ../ZenLead.Api/wwwroot && mkdir -p ../ZenLead.Api/wwwroot && cp -r dist/ZenLead.Client/browser/. ../ZenLead.Api/wwwroot/

      - name: Publish
        run: dotnet publish ZenLead.Api -c Release -o publish -p:SkipClientBuild=true

      - name: Build EF migrations bundle
        run: |
          dotnet tool install --global dotnet-ef
          dotnet ef migrations bundle -p ZenLead.Infrastructure -s ZenLead.Api --self-contained -r linux-x64 -o efbundle --force

      - uses: azure/login@v2
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}

      - name: Apply migrations (Azure SQL, Entra auth via the logged-in CLI identity)
        run: ./efbundle --connection "Server=tcp:${{ vars.SQL_SERVER }}.database.windows.net,1433;Database=zenlead;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=90;"

      - uses: azure/webapps-deploy@v3
        with: { app-name: '${{ vars.WEBAPP_NAME }}', package: publish }

      - name: Smoke check
        run: |
          for i in $(seq 1 18); do
            code=$(curl -s -o /dev/null -w '%{http_code}' https://${{ vars.PUBLIC_HOSTNAME }}/health || true)
            [ "$code" = "200" ] && echo "healthy" && exit 0
            echo "attempt $i: $code"; sleep 10
          done
          echo "::error::Smoke check failed — see Azure log stream; roll back via workflow_dispatch with the previous ref"; exit 1
```
Details to settle while building it: the SQL server's firewall must allow the GitHub runner (the "AllowAzureServices" rule does not cover GitHub-hosted runners — either add the runner's IP for the run with `az sql server firewall-rule create/delete`, or run migrations from the app host; the first is simpler and is scripted in the workflow with an `if: always()` cleanup step); `vars.*` are repository **variables** (non-secret IDs), the federated credential subject is `repo:Zenrax-Consulting/ZenLead:ref:refs/heads/main` (and `environment:production` if the environment is used); the CI identity needs `Website Contributor` on the web app and `db_owner` in the database (created like the app identity in `grant-app-identity.sql`).

**d) Branch protection** (documented command, run once by the repo owner): require the `build-test` check, PRs for `main`, up-to-date branches, no force pushes:
`gh api -X PUT repos/Zenrax-Consulting/ZenLead/branches/main/protection --input protection.json` (`protection.json` committed under `infra/github/`).

### 30.4 — Health, observability baseline, resilience

- **`/health`** — `builder.Services.AddHealthChecks().AddDbContextCheck<ZenLeadDbContext>("database")`; mapped with `app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = (ctx, _) => ctx.Response.WriteAsync(ctx.Response.StatusCode == 200 ? "ok" : "unhealthy") }).AllowAnonymous().DisableRateLimiting()` — **minimal body, no details** (details would leak infrastructure info to anonymous callers). App Service `healthCheckPath` points at it. Added to the F29 anonymous allow-list ("liveness for the platform and the pipeline") and the matrix doc.
- **30.4-b — Retry strategy + `EfUnitOfWork`:**
```csharp
options.UseSqlServer(cs, sql => sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null));

public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
{
    var strategy = db.Database.CreateExecutionStrategy();
    return await strategy.ExecuteAsync(async () =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    });
}
```
Re-check every other manual transaction added in Phase 2 (F28 batch commit, F20 flows) for the same rule; add a test that a fake transient `SqlException` (number 40613/49918) during the action is retried once and the work is not applied twice (the `action` must be safe to re-run — it already must be, since the transaction rolled back). Hangfire's own SQL storage has its own retry handling.
- **Serilog → Application Insights:** package `Serilog.Sinks.ApplicationInsights` + `Microsoft.ApplicationInsights.AspNetCore`; in `UseSerilog(...)`: `if (config["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 }) cfg.WriteTo.ApplicationInsights(services.GetRequiredService<TelemetryConfiguration>(), TelemetryConverter.Traces);` and `builder.Services.AddApplicationInsightsTelemetry()` (request/dependency telemetry). Sampling left at the SDK default; daily cap on the workspace protects cost. F29's no-PII rules apply — telemetry initializers must not add user emails (`TelemetryInitializer` that sets `Cloud.RoleName = "zenlead-web"` and drops the query string from `RequestTelemetry.Url`).
- **Correlation id:** `CorrelationIdMiddleware` — `Activity.Current?.TraceId` (W3C, already set by ASP.NET Core) is pushed into `LogContext` as `TraceId` and returned in the `X-Correlation-Id` response header, so a user-reported error maps to logs and App Insights `operation_Id`.
- **HTTPS/HSTS/CORS review:** `UseHttpsRedirection` stays; `UseForwardedHeaders` (F29) runs first; HSTS only in Production; no CORS; `AllowedHosts` set; confirm the `Secure` flag on the ops cookie works behind the TLS-terminating front end (needs the forwarded-proto header).

### 30.5 — Cost tracking
`budget.bicep` (deployed in 30.1) alerts the owner at 50 % / 80 % actual and 100 % forecasted of **$40**; **OpenAI** per-environment hard cap set in the OpenAI dashboard (record the value in `infra/README.md`, not the key); `docs/cost-tracking.md` — a one-table weekly check (Azure Cost Analysis, SendGrid usage, OpenAI usage, Azure SQL free-offer vCore-seconds remaining) with the first-upgrades order from parent §1a. F31.3 compares actuals to this.

### 30.6 — First production deploy & parity checklist
Do the steps in order and tick each in `docs/prod-parity-checklist.md`:
1. `scripts/deploy.ps1` (what-if reviewed) → resources exist; `grant-app-identity.sql` run; secrets set; GitHub OIDC variables set; `gh` branch protection applied.
2. **DNS (parent §1b):** at GoDaddy add `leads` CNAME → `<app>.azurewebsites.net` and `asuid.leads` TXT (value from `az webapp show --query customDomainVerificationId`); then `az webapp config hostname add …` and a free **managed certificate** (`az webapp config ssl create`, then `ssl bind`). Wait for DNS; verify `https://leads.zenraxconsultancy.com/health` → `ok` over a valid certificate. (Managed certs require B1+ — already the plan SKU.)
3. **Pipeline:** merge to `main` → green deploy → migrations applied **from scratch** to the empty Azure SQL database (also: restore a copy of the dev DB to a scratch database and run the bundle against it to prove upgrade-from-existing works; delete the scratch DB).
4. **Hangfire on Azure SQL:** `/hangfire` via the ops cookie shows the `campaign-sender` recurring job and the `MaintenanceJob`; **restart the web app** (`az webapp restart`) with a queued job and confirm it resumes and the recurring job continues. Record the free-offer **vCore-seconds consumed per day** for a week (metric on the database) — if Hangfire's polling keeps it awake and the allowance is on course to be exhausted, flip `useSqlFreeOffer = false` (Basic, ≈ $5/month) and redeploy; also confirm the first request after an auto-pause succeeds (retry/timeout settings above).
5. **Blob:** upload a CSV through the production UI → the blob appears in `zenlead/imports/` (verify in the portal), import completes; confirm there are **no storage keys** anywhere (`allowSharedKeyAccess=false`; app uses the managed identity).
6. **Key Vault:** startup log shows the vault URI and the *count* of loaded secrets, never values; remove one required secret in a scratch deployment and confirm the app **fails fast** with the F30.2 message.
7. **Email loop on production URLs:** switch SendGrid **Inbound Parse** and **Event Webhook** URLs from the dev tunnel to `https://leads.zenraxconsultancy.com/api/v1/webhooks/sendgrid/...`; set the real secrets in the vault; set `Email__DomainVerified` if not already; **send a real campaign email, reply, and see the reply in the production inbox with classification and events updating** (opens, delivered).
8. **Security spot-checks against prod:** security headers + CSP (no violations across screens), `/hangfire` blocked without the cookie, anonymous `GET /api/v1/leads` → 401, `GET /api/v1/admin/workspaces` as a regular user → 403, super admin created from the vault secrets (and can't see tenant data).
9. Log stream / App Insights: requests, dependencies, traces arrive; **no PII** (search for the test addresses); `X-Correlation-Id` matches `operation_Id`.
10. Perf spot-check vs `docs/perf-baseline.md` (leads list, inbox, analytics against the seeded prod-like dataset **in a scratch environment only** — never seed fake data into production).

### 30.7 — Azure-dependent hardening moved from F29
- **Alerts (`alerts.bicep`)**, all to the owner's email via an action group: **failed Hangfire jobs** (scheduled KQL query over `traces` for `Hangfire` errors, `> 0` in 15 min); **5xx rate** (metric alert on `requests/failed` or KQL: failed requests ≥ 5 in 5 min); **webhook failures** (KQL: requests to `/api/v1/webhooks/` with result 401/5xx ≥ 3 in 15 min — a signature-key mismatch looks exactly like this); **daily send failures** (KQL over the sender's `Sender run:` log line: `Failed + Errors` ≥ threshold per day, and *no* `Sender run` trace for 10 minutes while the app is up = the recurring job died); **availability** (App Insights availability test on `/health`, or a standard ping test).
- **Azure SQL backup / PITR test restore:** `az sql db restore --dest-name zenlead-restore-test --time <15 minutes ago>` → connect, compare row counts of `Leads`/`Campaigns`/`EmailMessages` with production, then delete the test database; record date and RTO observed in `docs/prod-parity-checklist.md`.
- **Blob lifecycle verification:** confirm the policy exists (`az storage account management-policy show`), then prove it once with a temporary rule on a `zenlead/lifecycle-test/` prefix (`daysAfterModificationGreaterThan: 0`, lifecycle runs ~daily): upload a test blob, wait for removal, **delete the temporary rule**. Record in the checklist.

## Tests
- **`StartupConfigurationTests`** — the production rules (each missing/invalid key → a message naming the key; `AllowedHosts=*` rejected; `Provider=Log` rejected; super-admin rule from F19; `LeadSource:Provider=Fake` rejected in Production).
- **`EfUnitOfWorkTests`** (SQLite can't simulate `SqlException`): a custom `IExecutionStrategy`/`ExecutionStrategy` subclass that retries once proves `ExecuteInTransactionAsync` runs the transaction inside the strategy (no `InvalidOperationException: … does not support user-initiated transactions`) and that a rolled-back first attempt leaves no rows.
- **`HealthEndpointTests`** (`ApiFactory`) — `GET /health` anonymous → `200 "ok"` with no details; unhealthy database (swap in a failing check) → `503 "unhealthy"`, no exception text; the endpoint is on the anonymous allow-list.
- **`CorrelationIdMiddlewareTests`** — header present and equal to the `Activity` trace id; id appears in the log context.
- **Bicep:** `az bicep build` + `az deployment group what-if` in CI (non-deploying) for `infra/` changes — catches syntax and drift; PSScriptAnalyzer on `infra/scripts`.
- **Workflow lint:** `actionlint` step in CI for `.github/workflows`.
- **Pipeline dry run:** run `ci.yml` on a branch with a deliberately failing test (red), then fix (green) — confirms the gate before it is made *required*.

## Not in this feature
Staging slot, VNet integration/private endpoints, Front Door/WAF, multi-region, autoscale, dedicated SendGrid IP, infrastructure for other environments (a second `main.bicepparam` is the extension point), blue/green migrations, Terraform, container images.

## Verification
The 30.6 checklist **is** the verification; the feature is done when every row is ticked in `docs/prod-parity-checklist.md` with a date, `main` is protected, a failed smoke check has been demonstrated to fail the pipeline (deploy a deliberately broken `/health` to a throw-away branch/env once), rollback via `workflow_dispatch` has been rehearsed once, and alerts have each been triggered or test-fired at least once.
