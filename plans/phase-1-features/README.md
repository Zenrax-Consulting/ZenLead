# Phase 1 — Per-Feature Detailed Plans

Detailed, file-by-file implementation plans for each feature in [phase-1-poc-implementation-plan.md](../phase-1-poc-implementation-plan.md). Each doc lists exact files to add/modify and the code to put in them. Read the parent plan first for context (goals, locked decisions, Gate 1 criteria) — these docs assume it.

| # | Feature | Branch | Milestone |
|---|---|---|---|
| 1 | [Domain & Persistence Foundation](01-domain-entities-dbcontext.md) | `feature/domain-entities-dbcontext` | 1 |
| 2 | [Auth & JWT Backend](02-auth-jwt.md) | `feature/auth-jwt` | 1 |
| 3 | [Angular Auth Experience](03-angular-auth.md) | `feature/angular-auth` | 1 |
| 4 | [Milestone 1 End-to-End Proof](04-e2e-proof-milestone1.md) | `feature/e2e-proof-milestone1` | 1 |
| 5 | [Semantic Kernel Wiring](05-semantic-kernel-wiring.md) | `feature/semantic-kernel-wiring` | 2 |
| 6 | [Compose-Email Use Case](06-compose-email-use-case.md) | `feature/compose-email-use-case` | 2 |
| 7 | [Compose Endpoint](07-compose-endpoint.md) | `feature/compose-endpoint` | 2 |
| 8 | [Angular Lead-Detail & Draft Generation UI](08-angular-lead-detail.md) | `feature/angular-lead-detail` | 2 |
| 9 | [Cost Logging & AI Test Coverage](09-cost-logging-polish.md) | `feature/cost-logging-polish` | 2 |
| 10 | [Gate 1 Hardening & Demo Readiness](10-gate1-hardening.md) | `feature/gate1-hardening` | 3 |

Each feature depends on the one before it unless noted otherwise in its doc — build and merge in order.
