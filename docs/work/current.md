# Current work handoff

Updated: 2026-10-03 (Asia/Seoul). Snapshot, not live status. Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Current snapshot

- Application code baseline: [`fa5b4ef2fee06b4b24cad0ad18b1577fe78dcac6`](https://github.com/fishbowl92/coop-game-server/commit/fa5b4ef2fee06b4b24cad0ad18b1577fe78dcac6), committed and pushed to `main`. This closes the designated review items after the earlier matchmaking/completion recovery slice `4b3dcb1`.
- Completed fixes: reward integer-capacity rejection with complete rollback; shared PostgreSQL player-row locking for solo/party participation and cross-queue solo races; original enqueue replay after cancellation/party join/restart; nested cache validation and independent payload expiry; player-scoped room request budgets; administrator profile refresh and HTTP 422 handling.
- Full local Release build/regression, style/analyzers, four image builds, Compose startup and the representative HTTP demo succeeded on 2026-10-02 at the unchanged application source later published as `fa5b4ef`. Detailed files/symbols/commands and verification limits are in the [final code review report](../engineering/2026-10-03-final-review-fixes.md).
- [Exact-code CI run 37039655073](https://github.com/fishbowl92/coop-game-server/actions/runs/37039655073) succeeded on 2026-10-03 (Asia/Seoul): restore, style, analyzers, dependency audit, Release build, full regression and four image builds. A later documentation-only handoff commit has identical application/test source; recheck the latest remote HEAD and its own CI when resuming.
- Existing migration `20261001092734_AddDurableMatchmakingOperations` was applied successfully to the preserved development PostgreSQL data on 2026-10-02. This final slice adds no migration. The pre-migration backup and local execution results remain under ignored `artifacts/verification-20261002/`.
- Week 9's single-machine Compose package provides API, Silo, Admin, PostgreSQL, Redis, Aspire Dashboard, and one-shot Migrator. See the [release package](../design/week-09-release-package.md).
- On 2026-10-03, API 5265, Silo readiness 5266 and Admin 5275 each returned HTTP 200 / Healthy. Existing data volumes remain intact.

## Documentation work

- The 2026-10-02 Notion review updated 19 existing pages while preserving child pages and learner answer/evaluation records.
- On 2026-10-03, updated and fetched back five status pages: dashboard, roadmap, cache, observability and public portfolio. All targeted edits were present, and child page/database references were preserved. Closed the implemented review items, linked the final report and exact-code CI, retained evidence dates/limits, and corrected the API/Silo port labels.
- The [Notion development dashboard](https://www.notion.so/3a3ff0d6971781479c5efa99000a9860) holds current completion status. Other weekly notes retain their dated implementation/verification history; an older evidence footer is not a new test run.

## Evidence boundaries

- [Week 8 measurements](../performance/week-08/README.md) were taken on 2026-09-23 at local implementation `fe61f83`, then published with report commit `76939f7`. They are not measurements of the latest recovery changes.
- Two load scenarios passed their stated baseline and post-load checks. No reproducible bottleneck was established, so no server optimization or After measurement was performed. Cache hit ratio and first-combat latency cause remain unisolated.
- Load used a single API/Silo and tmpfs PostgreSQL. It does not prove production capacity, disk behavior, or completed-room reward idempotency under load.
- [Redis outage evidence](../incidents/week-07-redis-outage.md) covers readiness during a controlled stop and a separate progression fallback integration test, not a combined trace/metric/latency incident experiment.
- The 2026-09-29 startup duration of 85.2 seconds used a Git archive of `c144608` and locally available base images. The latest Compose build/startup and representative demo succeeded, but that older duration was not measured again and excludes first-time downloads/setup on a new PC.
- Cache payload expiry limits each value's age, including when another page-size field refreshes the shared Hash key. It does not promise immediate freshness after every write or clock agreement across unrelated clusters.
- Room rate limits apply per authenticated player and policy in one API process. They are not shared across multiple API instances.
- The [demo script and shot plan](../portfolio/demo-and-interview-script.md) cover setup, party/match/connections, the first combat action, and administrator operations. No 3-5 minute video exists; the automated demo does not complete an entire game.

## Outstanding decisions and next action

- No designated code correction, required local verification, or focused Notion status update remains from this review. Code was committed/pushed and its exact-code CI passed. Recheck volatile publication/service facts on the next task.
- Record and review the 3-5 minute video separately, then link an actual file or public URL. The script alone is not a completed recording.
- Multi-Silo membership, production TLS, external secret management, backup/recovery, and sustained operational load remain outside the verified local portfolio environment.
- Preserve the pre-existing untracked `CoopGameServer/` review-data directory and existing Docker data. Cleanup is not required to continue.
