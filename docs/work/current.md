# Current work handoff

Updated: 2026-09-29 (Asia/Seoul). Snapshot, not live status. Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 9 implementation is complete locally. The execution contract is [Week 9 release package](../design/week-09-release-package.md).
- Runtime commit `50ab5da` contains the container, health, migration, CI, and test changes. Documentation commit `c144608` contains the README, diagrams, incident report, demo, and interview material.
- API, Silo, Admin, and the one-shot Migrator have multi-stage .NET 10 Alpine Dockerfiles. Final containers use the image-provided non-root user; Npgsql images include the optional GSSAPI runtime library so startup logs stay clean.
- Compose profile `app` provides PostgreSQL, Migrator, Redis, Silo, API, Admin, and Aspire Dashboard with dependency ordering and loopback-only host ports. API/Silo/Admin expose separate liveness and readiness endpoints.
- CI now restores, checks style and analyzers, audits direct/transitive NuGet packages, builds/tests Release, and builds all four application images.
- The representative HTTP demo, system/ERD/sequence document, Redis outage report, and interview script are tracked. Default WeatherForecast files and the obsolete `.http` sample were removed.

## Local verification

- Release solution build: 13 projects, 0 warnings, 0 errors.
- Complete regression: 141 unit + 140 integration tests passed, 0 failed/skipped. The new HTTP test proves anonymous liveness plus PostgreSQL-and-Orleans readiness through the real ASP.NET Core pipeline.
- NuGet vulnerability inspection: no known direct or transitive vulnerable package reported in 13 projects.
- Four application images built successfully. Migrator applied the two pending local migrations on the first start, then reported zero pending migrations on the second start.
- API, Silo, and Admin readiness returned `Healthy`; API liveness returned `Healthy`. Silo, API, Admin, and Migrator ran as container user `1654`; Migrator exited 0.
- During a controlled Redis stop, API and Silo readiness remained `Healthy`; Redis returned to `healthy` after restart.
- `Invoke-PortfolioDemo.ps1` completed four account registrations, a four-player party match, four connections, combat start, first attack, administrator reward, player lookup, and reward-history verification without printing tokens.
- A Git archive of `c144608` started the complete stack from committed files only in 85.2 seconds on the local warm-image environment. The same seven-stage demo passed against that clean snapshot. Temporary containers and source files were removed; test volumes were preserved.
- `docker compose config -q`, tracked document target checks, and `git diff --check` passed. The pre-existing untracked `CoopGameServer/` review-data directory remains untouched.

## Boundaries and next action

- The Compose package is a single-machine, single-Silo portfolio environment. It does not prove production TLS, secret management, multi-node membership, rolling migration, backup, or capacity.
- Redis outage evidence covers readiness and the existing progression fallback test, not sustained latency or an external alert route.
- Week 8's cache hit ratio and first-combat latency cause remain unisolated. Week 8 load is not a production capacity claim.
- A 3-5 minute recording is still a manual artifact. The tracked script and shot plan are ready, but Week 9 must not claim that the video exists until a file or public URL is reviewed.
- The Notion roadmap and Week 9 page were stale at implementation start and still require exact-commit synchronization after publication.
- Next: publish the three purpose-based commits, verify GitHub Actions at the public commit, then update the existing Notion Week 9 and roadmap pages.
