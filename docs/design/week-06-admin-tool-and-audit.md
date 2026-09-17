# Week 6 administrator tool and auditable rewards

Status: implemented and locally validated. Date: 2026-09-18.

## Outcome

An authenticated administrator can sign in to a local Blazor tool, find a Player by
ID or exact nickname, inspect the first progression page and recent reward history,
and grant a reward with a required reason. The durable write records who requested
the reward in the same PostgreSQL transaction as the reward effect and reward audit.

## Baseline

- `AdministratorOnly` already protects the reward endpoint.
- The API already derives Player identity and role from a validated JWT.
- `RewardService` sends administrator rewards through `PlayerGrain`.
- `PostgreSqlRewardWriter` atomically writes `reward_audits`, wallet, and inventory.
- Redis progression invalidation already follows durable reward success.
- Missing behavior: administrator account identity is not persisted, nickname search
  and audit queries do not exist, and there is no administrator UI.

## Scope

Included:

- Require a valid administrator account ID claim for administrator-only APIs.
- Carry that account ID through API, Orleans, and persistence reward contracts.
- Add immutable `admin_audits` rows for administrator rewards.
- Store reward effect, reward audit, and administrator audit in one transaction.
- Treat a replay as the same request only when reward data and administrator identity
  both match the persisted request.
- Add administrator Player lookup and reward-history HTTP endpoints.
- Add a server-side Blazor administrator project that calls only authenticated APIs.
- Keep one request ID across failed retries and create a new ID after confirmed success.
- Add domain, persistence, Grain, HTTP authorization, and UI-state tests.

Excluded:

- Direct database or Grain access from the Blazor project.
- Bulk rewards, approval workflows, SSO, MFA, and fine-grained RBAC.
- Editing or deleting audit records.
- Production administrator provisioning. A development-only optional bootstrap uses
  User Secrets and refuses to promote an existing Player account.

## Ownership

- API: JWT authentication, administrator policy, claim parsing, search and response
  mapping.
- Blazor Admin: form state, API calls, token held in the server-side circuit, and
  request ID reuse.
- PlayerGrain: serialize reward commands for one Player and preserve cache invalidation.
- PostgreSQL: reward correctness, idempotency, account and Player references, and
  atomic `reward_audits` plus `admin_audits` persistence.
- Redis: deletable progression cache only; it never proves administrator reward success.

## Failure model

- Invalid or missing administrator identity is rejected before the Grain call.
- Missing Player or invalid reward input writes neither reward nor administrator audit.
- A database error before commit rolls back wallet, inventory, reward audit, and
  administrator audit together.
- A response loss after commit is retried with the same request ID. The persisted
  reward and administrator identities must match before the original receipt is replayed.
- Redis invalidation failure does not roll back the committed PostgreSQL transaction.
- Client cancellation after the Grain call begins does not cancel the durable write.

## Acceptance

- Domain tests cover administrator audit validation.
- Real PostgreSQL tests cover atomic success, rollback, replay, identity conflict, and
  migration constraints.
- Orleans tests cover command validation and persisted administrator identity.
- `WebApplicationFactory` tests cover 401, 403, missing/invalid account claims,
  administrator success, nickname lookup, reward history, and idempotent replay.
- Admin state tests prove one request ID is reused until confirmed success.
- Release solution build and the complete unit and integration suites pass.

## Commit grouping

1. Administrator audit domain, migration, reward contract, and persistence tests.
2. Administrator query APIs, authorization tests, and development bootstrap.
3. Blazor administrator UI and UI-state tests.
4. Week 6 learning documentation and current-work handoff.

## Implementation record

- The administrator account claim now flows through `RewardsController`, `RewardService`,
  `GrantPlayerRewardCommand`, `PlayerGrain`, and `RewardWriteCommand`.
- `admin_audits` has immutable actor, target, request, action, payload, result, and time
  fields plus account, Player, and reward-audit references.
- `AdminOperationsController` provides exact Player lookup and recent reward history.
- `CoopGameServer.Admin` is a server-side Blazor API client with retry-safe request state.
- Development bootstrap is opt-in through API User Secrets and never promotes an existing
  ordinary account or reuses an existing Player nickname.
- The Korean code walkthrough is [Week 6 learning note](week-06-admin-tool-and-audit.ko.md).