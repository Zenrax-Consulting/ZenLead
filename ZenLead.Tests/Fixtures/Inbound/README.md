# Inbound Parse fixtures (F25)

Field dictionaries in the shape SendGrid's Inbound Parse posts (parsed-fields mode; `raw-mime.json` is raw-MIME mode).

**All current files are hand-written synthetic payloads** modelled on each client's real header/quoting style, because the live
Inbound Parse host was not ready when F25 was built. Replace or add real ones as they are collected with `Webhooks:CaptureRaw=true`
(Development only; files land in `%TEMP%\zenlead-inbound`) — **replace personal data before committing**.

Placeholders substituted by the tests: `__ENROLLMENT__` (enrollment id, `N` format) and `__LEAD__` (the lead's email).
The inbound domain is `reply.zenlead.test`.
