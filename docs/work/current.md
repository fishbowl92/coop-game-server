# Current work handoff

Updated: 2026-09-16 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 5 Player progression Redis Cache-Aside is implemented and locally validated.
- Local teaching record: [Week 5 Redis progression cache](../design/week-05-redis-progression-cache.md).
- Notion teaching page: [Week 5 learning note](https://app.notion.com/p/3caff0d6971781fa9e51e7f099994498), updated to implementation-complete status on 2026-09-16.
- PostgreSQL remains the durable source of truth. Redis is a deletable first-page read cache.

## Checkout snapshot

- Root: `C:\Users\Administrator\source\repos\CoopGameServer`.
- Branch: `main`.
- Baseline before this task: `b3315c2`.
- Agent guide commit: `6f91409`.
- Week 5 implementation commit: `c5ee4bc`.
- Week 5 test commit: `2932c6d`.
- Pre-existing untracked Notion review data under `CoopGameServer/notion-review/2026-09-07/` remains untouched and must stay out of task staging.

## Implemented behavior

- `GET /api/players/{playerId}/progression` returns the authenticated Player profile, gold, and inventory page.
- The first page is cached in a versioned Redis Hash field for each page size.
- Redis miss, connection error, operation timeout, or corrupt payload falls back to a PostgreSQL Repeatable Read snapshot.
- Reward application, reward replay, game completion reward, and nickname change invalidate the Player cache after durable success.
- Redis read, write, and invalidation failures do not replace PostgreSQL success or exactly-once reward evidence.
- Cache options define the key prefix, two-minute TTL, up to twenty seconds of jitter, and a 100 ms operation timeout.
- Metrics cover request result, fallback reason, Redis errors and duration, and database fill duration without Player ID labels.

## Validation observed now

- `dotnet build CoopGameServer.slnx --configuration Release --no-incremental` passed with zero warnings and errors.
- `dotnet test CoopGameServer.slnx --configuration Release --no-build` passed for the complete unit and integration suites.
- Integration validation used real PostgreSQL and Redis Testcontainers.
- Targeted tests passed for corrupt cache repair, nickname-driven key deletion, and Redis-unavailable query, reward, and replay behavior.
- Task C# files pass `dotnet format --verify-no-changes` when scoped with `--include`.
- Repository-wide format verification still reports pre-existing line-ending, encoding, and import-order findings outside this task. Do not normalize unrelated files as part of Week 5.

## Outstanding boundaries

- No push or remote CI run has been performed for the new local commits.
- Exact delayed-GET timeout injection, individual Redis SET/DEL failure injection, and concurrent-miss load measurement remain operational test extensions.
- The TTL and timeout are initial values; tune them only from observed latency, fallback, and database-load evidence.
- Metrics export and dashboards remain Week 7 scope. Session, distributed rate-limit, and idempotency lookup caches remain outside this slice.

## Next action

Push the four local commits only within explicit authorization, then bind remote CI evidence
to the pushed commit. Start Week 6 from the accepted roadmap after preserving this cache
correctness boundary.
