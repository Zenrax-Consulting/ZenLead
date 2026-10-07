# Gate 1 browser rehearsal

Dev-only Playwright script (uses your installed Google Chrome; nothing is downloaded). It walks the real UI: register, empty state, duplicate email, logout, login, add lead, browser refresh (silent re-auth), generate draft (timed), back link, and signed-out redirect. It makes one real OpenAI call.

```
# 1. fresh database + API (set a throwaway DB name; the connection string comes from user-secrets otherwise)
$env:ConnectionStrings__Default = 'Server=(localdb)\mssqllocaldb;Database=ZenLeadFreshCheck;Trusted_Connection=True;'
dotnet ef database update -p ZenLead.Infrastructure -s ZenLead.Api
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:ASPNETCORE_URLS = 'http://localhost:5199'
dotnet run --no-launch-profile --project ZenLead.Api

# 2. Angular dev server on HTTP, proxying /api to the API (reads ASPNETCORE_URLS)
cd ZenLead.Client
$env:ASPNETCORE_URLS = 'http://localhost:5199'
npx ng serve --port 4300 --proxy-config src/proxy.conf.js

# 3. rehearsal
cd tools/gate1-rehearsal
npm install
node rehearsal.mjs http://localhost:4300 ../../docs/gate1-evidence/browser
```

Screenshots and `results.json` are written to the output folder. The exit code is non-zero if any step fails.
