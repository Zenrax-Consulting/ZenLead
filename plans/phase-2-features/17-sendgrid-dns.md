# Feature 17 — SendGrid Domain & Inbound DNS Kickoff

**Branch:** `chore/sendgrid-dns` (docs and one small config/code change; no feature code)
**Sprint:** 1 — **start on day one**, finish in week 4. Sprint 3's real-inbox demo and Sprint 4's inbound parse depend on it, and DNS propagation plus SendGrid verification can take days.
**Depends on:** nothing in code. Owner needs access to the GoDaddy DNS for `zenraxconsultancy.com` and a SendGrid account.

## Goal
Everything non-code that email needs, done early: SendGrid account + restricted API key, domain authentication for `leads.zenraxconsultancy.com`, the inbound subdomain `reply.leads.zenraxconsultancy.com` with its MX record, the Inbound Parse destination placeholder, the sender-identity decision, and a record of DNS status in `docs/`. Layout of records is the parent plan's §1b — this doc is the runbook.

## Runbook

### 1. SendGrid account and API key (PBI 17.1)
1. Create the account (plan must include **Inbound Parse** and **Event Webhook** — see parent plan §1a; the 60-day trial is fine for Sprints 1–3 but note its end date in `docs/sendgrid-dns-status.md`).
2. **Settings → API Keys → Create** a **restricted** key with only **Mail Send** (full access = a leaked key can read/alter everything). Store it: `dotnet user-secrets set "SendGrid:ApiKey" "<key>" --project ZenLead.Api`. Never commit it, never paste it into chat or `.http` files.
3. **Settings → Tracking:** *Click tracking* **off** (rewritten links hurt cold-email deliverability and obscure our unsubscribe URL); *Subscription tracking* **off** (the app injects its own unsubscribe link + `List-Unsubscribe` header in F22/F23); *Open tracking* **on** (F28 reads it; treat as approximate).

### 2. Domain authentication for `leads.zenraxconsultancy.com` (PBI 17.1)
1. **Settings → Sender Authentication → Authenticate Your Domain** → DNS host *GoDaddy* → domain `zenraxconsultancy.com`, **"Use automated security" on**, **custom return path** on, advanced: *use a subdomain* `leads` (so records land under `leads.`, isolating cold-outreach reputation from the root domain's mail).
2. SendGrid shows 3 CNAMEs (shapes: `em####.leads`, `s1._domainkey.leads`, `s2._domainkey.leads`). At GoDaddy → DNS → Add, **Name = only the part before `.zenraxconsultancy.com`** (e.g. `s1._domainkey.leads`), type CNAME, TTL 1 hour. Copy values exactly; GoDaddy sometimes appends the domain itself — if the verify step fails, check for a doubled `.zenraxconsultancy.com.zenraxconsultancy.com`.
3. Click **Verify** in SendGrid. Not verified after an hour → check with `nslookup -type=CNAME s1._domainkey.leads.zenraxconsultancy.com 8.8.8.8` (or `dig +short`). Record the outcome (§5).

### 3. Inbound subdomain and Inbound Parse (PBI 17.2)
1. GoDaddy → add **MX**: Name `reply.leads`, Value `mx.sendgrid.net`, Priority `10`, TTL 1 hour. (A name that has a CNAME cannot also have MX/TXT — that is why inbound lives on `reply.leads`, not `leads`. This MX does not touch the root domain's mail.)
2. SendGrid → **Settings → Inbound Parse → Add Host & URL**: receiving domain `reply.leads.zenraxconsultancy.com`, destination URL = **placeholder for now** (`https://example.invalid/api/v1/webhooks/sendgrid/inbound?secret=PLACEHOLDER`). Leave **"POST the raw, full MIME message" off** (F25 parses the standard multipart fields; raw MIME is a fallback only), spam check optional.
3. The real URL is set in Sprint 4 (F25) to the **persistent dev tunnel** (parent §1c) and finally to `https://leads.zenraxconsultancy.com/…` in F30. The `secret` value is generated then and stored as `SendGrid:InboundSecret` in user-secrets.
4. Check propagation: `nslookup -type=MX reply.leads.zenraxconsultancy.com 8.8.8.8` → `mx.sendgrid.net`.

### 4. Root-domain mail authentication (check, don't break)
- If `zenraxconsultancy.com` already has SPF/DMARC (Microsoft 365/Google), **leave SPF alone** — outreach authenticates via the `leads.` DKIM/return-path CNAMEs, not root SPF. Inspect the existing DMARC: `nslookup -type=TXT _dmarc.zenraxconsultancy.com`. If none exists, add `_dmarc` TXT `v=DMARC1; p=none; rua=mailto:<owner mailbox>` (monitor first; move to `quarantine` only after reports look clean). If one exists, don't replace it — note its policy in the status doc; a strict root policy applies to the subdomain unless `sp=` says otherwise, and SendGrid's DKIM alignment on `leads.` satisfies it.
- Never send outreach from the root domain.

### 5. Record status in `docs/sendgrid-dns-status.md` (new file)
```markdown
# SendGrid & DNS status
| Item | Value | Date | Status |
|---|---|---|---|
| SendGrid plan / trial end | … | … | … |
| API key (restricted, Mail Send) | created, stored in user-secrets | … | ✔ |
| Domain auth `leads.zenraxconsultancy.com` | em####/s1/s2 CNAMEs added | … | verified / pending |
| MX `reply.leads` → mx.sendgrid.net | prio 10 | … | resolves / pending |
| Inbound Parse host | reply.leads.zenraxconsultancy.com → placeholder | … | configured |
| DMARC (root) | existing policy: … | … | … |
| Tracking settings | click off, subscription off, open on | … | ✔ |
| Sender identity | see decision below | … | ✔ |
```
Never put keys, secrets or the inbound secret in this file.

### 6. Sender identity decision (PBI 17.3) — **needs the owner's confirmation before F22**
Proposed (and assumed by the F18/F22/F23 docs):
- **One verified sender for Zenrax**: `From = Email:FromName <Email:FromAddress>` with `FromAddress = outreach@leads.zenraxconsultancy.com` (any local part works once the domain is authenticated; no mailbox exists or is needed because replies go to the inbound subdomain). `Email:FromName` default `Zenrax`.
- **Reply-To** is *not* the From address: it is an address on `reply.leads.zenraxconsultancy.com` that encodes the thread (F23/F25).
- Consequence for the model: no `SenderIdentity` table and no `Campaign.FromSenderId`. `Campaign` gets an optional `FromName` (default: config value) and the address is global. This **replaces** the `FromSenderId` field in parent PBI 22.1; if per-workspace senders are wanted later, add the table then (already listed in the parent plan's deferred items).
- "Sender verified" in the F24 activation check = *domain authentication verified*, which the app reads from config flag `Email:DomainVerified` (set `true` by hand once SendGrid shows verified) — no call to SendGrid's API in the MVP.

### 7. Code/config touched in this branch (small)
- **`ZenLead.Api/appsettings.json`** — add non-secret defaults:
```json
"Email": { "FromAddress": "outreach@leads.zenraxconsultancy.com", "FromName": "Zenrax", "DomainVerified": false, "InboundDomain": "reply.leads.zenraxconsultancy.com" }
```
- **`README.md`** "Local setup" — add `SendGrid:ApiKey` (and later `SendGrid:InboundSecret`, `SendGrid:EventWebhookPublicKey`) to the user-secrets list, and a pointer to this runbook.
- **`ZenLead.Api/ZenLead.Api.http`** — nothing (no key in the repo).
- **`docs/sendgrid-dns-status.md`** — created as above.

## Acceptance (end of week 4)
- [ ] Restricted API key in user-secrets; tracking settings as above.
- [ ] `leads.zenraxconsultancy.com` shows **verified** in SendGrid (or the fallback below is in use and noted).
- [ ] `reply.leads` MX resolves to `mx.sendgrid.net`; Inbound Parse host created with a placeholder URL.
- [ ] `docs/sendgrid-dns-status.md` filled in; sender-identity decision confirmed by the owner.

## Fallback (parent plan risk table)
If verification drags: use SendGrid **Single Sender Verification** with a mailbox you control as `From` so Sprint 3 isn't blocked (set `Email:FromAddress` to it). It sends from a shared-reputation path and won't pass strict DMARC alignment — fine for internal tests, not for Gate 2 volume. Replies would then come to that mailbox rather than the inbound subdomain, so F25 can't be exercised until the MX is live — start the MX first.

## Not in this feature
The `leads` app CNAME and `asuid.leads` TXT (F30), the Event Webhook configuration and its public key (F28), dedicated IP, any warm-up automation.
