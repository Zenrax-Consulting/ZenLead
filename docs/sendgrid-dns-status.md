# SendGrid & DNS status

Tracks Feature 17 (SendGrid domain & inbound DNS kickoff). Runbook: `plans/phase-2-features/17-sendgrid-dns.md`; DNS layout: `plans/phase-2-mvp-implementation-plan.md` §1b.

> **Domain correction (2026-11-26):** the company domain is **`zenraxconsulting.com`** (GoDaddy, nameservers `ns25/ns26.domaincontrol.com`). The plans and the F17 runbook say `zenraxconsultancy.com`, which does not exist; read every `zenraxconsultancy.com` in them as `zenraxconsulting.com`. The sender is **`business@zenraxconsulting.com`** (owner decision), not `info@`.

**Never put API keys, the inbound secret or any other secret in this file.** The SendGrid key lives only in user-secrets (`dotnet user-secrets set "SendGrid:ApiKey" "<key>" --project ZenLead.Api`).

| Item | Value | Date | Status |
|---|---|---|---|
| SendGrid plan / trial end | 60-day free trial, ends 2027-01-24 (paid plan needed after that) | 2026-11-26 | ✔ active |
| API key (restricted, Mail Send) | created, stored in user-secrets | 2026-11-26 | ✔ |
| Domain auth `zenraxconsulting.com` (root) | `em4973`, `s1`/`s2._domainkey` CNAMEs all resolve to SendGrid (u119583210.wl211) on `zenraxconsulting.com`; but the SendGrid domain entry was created for `zenraxconsultancy.com`, so Verify fails — must be re-created for `zenraxconsulting.com` | 2026-11-26 | pending |
| MX `reply.leads` → mx.sendgrid.net | prio 10; `reply.leads.zenraxconsulting.com` does not resolve yet | 2026-11-26 | pending |
| Inbound Parse host | reply.leads.zenraxconsulting.com → placeholder (`https://example.invalid/api/v1/webhooks/sendgrid/inbound?secret=PLACEHOLDER`), raw MIME off | — | pending |
| DMARC (root) | existing policy: `v=DMARC1; p=quarantine; adkim=r; aspf=r; rua=mailto:dmarc_rua@onsecureserver.net` (GoDaddy); duplicate `p=none` record removed, single record confirmed | 2026-11-26 | ✔ |
| Tracking settings | click off, subscription off, open on | 2026-11-26 | ✔ |
| Sender identity | see decision below | 2026-11-26 | ✔ |

## Sender identity decision (PBI 17.3)

Decided by the owner:

- **One verified sender for Zenrax.** `From = Email:FromName <Email:FromAddress>`, with `Email:FromAddress = business@zenraxconsulting.com` (root domain) and `Email:FromName = Zenrax` (defaults in `ZenLead.Api/appsettings.json`).
- **Reply-To is not the From address.** It is an address on `Email:InboundDomain` = `reply.leads.zenraxconsulting.com` that encodes the thread (F23/F25).
- **No `SenderIdentity` table and no `Campaign.FromSenderId`.** `Campaign` gets an optional `FromName` (default: the config value); the address is global. This replaces `FromSenderId` in parent PBI 22.1. Per-workspace senders stay deferred.
- **"Sender verified"** in the F24 activation check means *domain authentication verified*, read from `Email:DomainVerified`. It is set to `true` by hand once SendGrid shows `zenraxconsulting.com` verified; the MVP makes no call to SendGrid's API for this.
- **Existing root mail:** GoDaddy mail (MX `smtp`/`mailstore1.secureserver.net`) and SPF `v=spf1 include:secureserver.net -all` are left untouched; SendGrid mail aligns through DKIM and the custom return path, not root SPF.
- **Risk accepted:** outreach shares the root domain's reputation with normal company mail (`business@` and staff mailboxes). Mitigations: warm-up caps, suppression, one-click unsubscribe, verified-email gate, very low starting volumes, watching DMARC reports and bounce/complaint rates (F28). Moving to a `leads.` sending subdomain later is a config + DNS change.
- **`business@` mailbox:** people who reply to the From address instead of Reply-To land there. Who watches it: _to be decided by the owner_.

## Fallback in use

None. (If domain verification drags, Single Sender Verification with an owner-controlled mailbox can be used for Sprint 3 internal tests; record it here and set `Email:FromAddress` to it.)

## Log

- 2026-11-26 — Status doc created; sender identity recorded; `Email` defaults added to `appsettings.json`. Manual SendGrid/GoDaddy steps not started.
- 2026-11-26 — SendGrid account created (trial ends 2027-01-24); restricted Mail Send key stored in user-secrets; tracking set (click off, subscription off, open on).
- 2026-11-26 — First DNS check queried `zenraxconsultancy.com` (wrong name, does not exist), so its "no DKIM / no DMARC" result was invalid.
- 2026-11-26 — Re-checked `zenraxconsulting.com` via 8.8.8.8: s1/s2 DKIM CNAMEs present; two `_dmarc` TXT records (GoDaddy `p=quarantine` + new `p=none`) — delete the `p=none` one; `reply.leads` MX not present. Sender changed to `business@zenraxconsulting.com`; `Email:FromAddress`/`InboundDomain` updated.
- 2026-11-26 — Duplicate DMARC record removed. All three CNAMEs present on `zenraxconsulting.com`. SendGrid Verify failed because its domain entry is `zenraxconsultancy.com`; owner to delete it and authenticate `zenraxconsulting.com` instead. `reply.leads` MX still absent.
