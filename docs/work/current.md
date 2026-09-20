# Current work handoff

Updated: 2026-09-21 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 6 administrator Player lookup, audited reward grant, and Blazor operations tool are implemented and locally validated.
- Design contract: [Week 6 administrator tool and audit](../design/week-06-admin-tool-and-audit.md).
- Korean code walkthrough: [6주차 관리자 도구와 감사 가능한 보상 지급](../design/week-06-admin-tool-and-audit.ko.md).
- PostgreSQL remains the durable correctness boundary. Redis remains a deletable progression cache.

## Implementation snapshot

- `AdministratorOnly` requires both the Administrator role and a valid non-empty `account_id` Guid claim.
- The verified administrator Account ID flows through API, Orleans, and persistence reward contracts.
- `admin_audits` records actor, target, request, action, normalized reward, reason, result, and UTC time.
- Reward effect, `reward_audits`, and `admin_audits` commit in one PostgreSQL transaction.
- Replay equality includes administrator identity; another actor or payload receives an idempotency conflict.
- `GET /api/admin/players/lookup` finds one Player by Guid or exact nickname.
- `GET /api/admin/players/{playerId}/reward-history` returns recent system and administrator rewards.
- `CoopGameServer.Admin` calls only authenticated HTTP APIs and keeps one request ID until success is confirmed.
- Development administrator creation is opt-in through API User Secrets and does not promote existing Player accounts.

## Purpose-based commits

- `eb277eb` — administrator audit domain, migrations, atomic persistence, and persistence tests.
- `dcb7400` — administrator authorization, query APIs, development bootstrap, and HTTP tests.
- `596dc01` — Blazor administrator operations tool and retry-state tests.
- `6792d86` — Week 6 design, Korean code walkthrough, runbook, and initial handoff documentation.

## Local validation

- `dotnet format CoopGameServer.slnx --verify-no-changes --no-restore`: passed before commit grouping.
- `dotnet build CoopGameServer.slnx --configuration Release --no-incremental`: zero warnings and zero errors.
- `dotnet test CoopGameServer.slnx --configuration Release --no-build`: 118 unit and 138 integration tests passed, total 256.
- New integration coverage uses real PostgreSQL, Redis, Orleans TestCluster, and ASP.NET Core `WebApplicationFactory`.
- Blazor administrator home page smoke test returned HTTPS 200 at `https://localhost:7248/`.

## Publication status

- The four Week 6 purpose-based commits through `6792d86` are pushed to `origin/main`.
- GitHub Actions run [#35519994002](https://github.com/fishbowl92/coop-game-server/actions/runs/35519994002) passed for exact public commit `6792d86656009e489ec48dca701cddb24011bc96`.
- The Week 6 Notion learning page and project dashboard were synchronized to that public commit and CI evidence on 2026-09-21.

## Workspace note

- The pre-existing untracked `CoopGameServer/` review-data directory remains untouched and excluded from staging.
- Repository-wide formatting changes in 14 Week 6-unrelated tracked files were restored. No unrelated tracked changes remain.

## Outstanding boundaries

- The current reward-history endpoint returns at most 100 recent rows and does not yet expose continuation-token paging.
- Production administrator provisioning, SSO, MFA, fine-grained RBAC, approval workflows, and bulk rewards remain outside Week 6.
- Rejected attempts are represented by HTTP/security/diagnostic logs; only committed successful administrator operations are stored in `admin_audits`.
- Metrics export and dashboards remain Week 7 scope.

## Next action

Start Week 7 observability work by tracing one administrator reward request across API, Orleans, PostgreSQL, and cache invalidation, then add secret-safe structured logs and useful metrics.
