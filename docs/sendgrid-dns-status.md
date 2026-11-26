# SendGrid & DNS status

Tracks Feature 17 (SendGrid domain & inbound DNS kickoff). Runbook: `plans/phase-2-features/17-sendgrid-dns.md`; DNS layout: `plans/phase-2-mvp-implementation-plan.md` §1b.

**Never put API keys, the inbound secret or any other secret in this file.** The SendGrid key lives only in user-secrets (`dotnet user-secrets set "SendGrid:ApiKey" "<key>" --project ZenLead.Api`).

| Item | Value | Date | Status |
|---|---|---|---|
| SendGrid plan / trial end | 60-day free trial, ends 2027-01-24 (paid plan needed after that) | 2026-11-26 | ✔ active |
| API key (restricted, Mail Send) | created, stored in user-secrets | 2026-11-26 | ✔ |
| Domain auth `zenraxconsultancy.com` (root) | em####/s1/s2 CNAMEs; `s1._domainkey` not resolving yet | 2026-11-26 | pending |
| MX `reply.leads` → mx.sendgrid.net | prio 10, not added yet | — | pending |
| Inbound Parse host | reply.leads.zenraxconsultancy.com → placeholder (`https://example.invalid/api/v1/webhooks/sendgrid/inbound?secret=PLACEHOLDER`), raw MIME off | — | pending |
| DMARC (root) | none existed; to add `_dmarc` TXT `v=DMARC1; p=none; rua=mailto:<owner mailbox>` (monitor first) | 2026-11-26 | pending |
| Tracking settings | click off, subscription off, open on | 2026-11-26 | ✔ |
| Sender identity | see decision below | 2026-11-26 | ✔ |

## Sender identity decision (PBI 17.3)

Decided by the owner:

- **One verified sender for Zenrax.** `From = Email:FromName <Email:FromAddress>`, with `Email:FromAddress = info@zenraxconsultancy.com` (root domain) and `Email:FromName = Zenrax` (defaults in `ZenLead.Api/appsettings.json`).
- **Reply-To is not the From address.** It is an address on `Email:InboundDomain` = `reply.leads.zenraxconsultancy.com` that encodes the thread (F23/F25).
- **No `SenderIdentity` table and no `Campaign.FromSenderId`.** `Campaign` gets an optional `FromName` (default: the config value); the address is global. This replaces `FromSenderId` in parent PBI 22.1. Per-workspace senders stay deferred.
- **"Sender verified"** in the F24 activation check means *domain authentication verified*, read from `Email:DomainVerified`. It is set to `true` by hand once SendGrid shows `zenraxconsultancy.com` verified; the MVP makes no call to SendGrid's API for this.
- **Risk accepted:** outreach shares the root domain's reputation with normal company mail (`info@` and staff mailboxes). Mitigations: warm-up caps, suppression, one-click unsubscribe, verified-email gate, very low starting volumes, watching DMARC reports and bounce/complaint rates (F28). Moving to a `leads.` sending subdomain later is a config + DNS change.
- **`info@` mailbox:** people who reply to the From address instead of Reply-To land there. Who watches it: _to be decided by the owner_.

## Fallback in use

None. (If domain verification drags, Single Sender Verification with an owner-controlled mailbox can be used for Sprint 3 internal tests; record it here and set `Email:FromAddress` to it.)

## Log

- 2026-11-26 — Status doc created; sender identity recorded; `Email` defaults added to `appsettings.json`. Manual SendGrid/GoDaddy steps not started.
- 2026-11-26 — SendGrid account created (trial ends 2027-01-24); restricted Mail Send key stored in user-secrets; tracking set (click off, subscription off, open on).
- 2026-11-26 — DNS check via 8.8.8.8: `s1._domainkey` CNAME not found (not yet resolving); no `_dmarc` record exists on the root domain.
