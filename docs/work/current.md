# Current work handoff

Updated: 2026-09-17 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 5 Player progression Redis Cache-Aside is implemented, locally validated, pushed, and verified by remote CI.
- Local teaching record: [Week 5 Redis progression cache](../design/week-05-redis-progression-cache.md).
- Notion teaching page: [Week 5 learning note](https://app.notion.com/p/3caff0d6971781fa9e51e7f099994498), synchronized on 2026-09-17.
- PostgreSQL remains the durable source of truth. Redis is a deletable first-page read cache.

## Checkout snapshot

- Root: `C:\Users\Administrator\source\repos\CoopGameServer`.
- Branch: `main`.
- Baseline before Week 5: `b3315c2`.
- Agent guide commit: `6f91409`.
- Week 5 implementation commit: `c5ee4bc`.
- Week 5 base test commit: `2932c6d`.
- Week 5 operational-edge test commit: `7cd4a84`.
- Published source/test commit: `7cd4a841ce1dd3fcf434d62bfb6ecb5da324a8cf`.
- Remote evidence: [GitHub Actions CI #35196252235](https://github.com/fishbowl92/coop-game-server/actions/runs/35196252235), success on 2026-09-17.
- Pre-existing untracked review data contains 26 Markdown files under `CoopGameServer/notion-review/2026-09-07/`. It remains untouched and excluded from task staging.

## Implemented behavior

- `GET /api/players/{playerId}/progression` returns the authenticated Player profile, gold, and inventory page.
- The first page is cached in a versioned Redis Hash field for each page size.
- Redis miss, connection error, operation timeout, or corrupt payload falls back to a PostgreSQL Repeatable Read snapshot.
- Reward application, reward replay, game completion reward, and nickname change invalidate the Player cache after durable success.
- Redis read, write, and invalidation failures do not replace PostgreSQL success or exactly-once reward evidence.
- Cache options define the key prefix, two-minute TTL, up to twenty seconds of jitter, and a 100 ms operation timeout.
- Metrics cover request result, fallback reason, Redis errors and duration, and first-page database fill duration without Player ID labels.

## Validation observed now

- `dotnet format` verification passed for every C# file changed in the Week 5 completion.
- `dotnet build CoopGameServer.slnx --configuration Release --no-incremental` passed with zero warnings and errors.
- `dotnet test CoopGameServer.slnx --configuration Release --no-build` passed for the complete unit and integration suites.
- Integration validation used real PostgreSQL and Redis Testcontainers.
- A live Redis pause proved the cache operation timeout; a stopped isolated Redis proved SET and DEL failures stay inside the cache boundary.
- Twenty concurrent first-page requests for the same Player produced one database fill in the current Orleans cluster.
- Repository-wide format verification still reports pre-existing line-ending, encoding, and import-order findings outside this task. Do not normalize unrelated files as part of Week 5.

## Outstanding boundaries

- TTL and timeout values are initial operating values; tune them from observed production-like latency, fallback, and database-load evidence.
- Metrics export and dashboards remain Week 7 scope.
- Redis authentication, TLS, and managed-service configuration remain deployment scope.
- Session, distributed rate-limit, and idempotency lookup caches remain outside this slice.
- Multi-cluster and abnormal duplicate-activation behavior require a deployment topology before meaningful load validation.

## Next action

Week 5 is closed. Start Week 6 from the accepted roadmap while preserving the PostgreSQL
correctness boundary and the Redis failure behavior recorded here.
