# Week 9 release package

Updated: 2026-09-29 (Asia/Seoul)

## 1. Outcome

Week 9 turns the verified learning server into a reproducible portfolio package. A reviewer must be able to build the same source, start the complete local stack with one Compose command, observe liveness and readiness, and follow the public authentication, game, reward, and administration paths without guessing hidden setup steps.

This slice connects existing Week 1-8 guarantees to a deployable boundary. It does not add new combat or progression rules.

## 2. Verified starting point

- Local `main`, `origin/main`, and the successful GitHub Actions run all point to `35d667e` before this work starts.
- The solution contains API, Orleans Silo, Blazor administrator UI, PostgreSQL persistence, Redis cache, tests, and the Week 8 load runner.
- The existing Compose file starts PostgreSQL, Redis, and Aspire Dashboard only. It has no application images or migration owner.
- API and Silo use localhost Orleans configuration. API, Silo, and Admin have no container health endpoints.
- CI restores, builds, and tests, but does not check style, package vulnerability output, or image builds.
- The Notion Week 9 page and roadmap still cite the older `f186650` era. Their historical status is discovery context, not an implementation baseline.
- `dotnet format analyzers --verify-no-changes` passes. `dotnet format style --verify-no-changes` finds one import-order violation in `PartiesController`; this work fixes that baseline before adding the CI gate.

## 3. Ownership and failure order

| Component | Responsibility | Start/failure rule |
| --- | --- | --- |
| PostgreSQL | Durable game and account state | Must be healthy before migration |
| Migrator | Apply EF Core migrations once | Exits nonzero on failure; Silo must not start |
| Redis | Progression cache only | Silo may run through Redis outages and fall back to PostgreSQL |
| Silo | Grain execution and recovery worker | Ready only after its durable dependencies and Orleans host start |
| API | Authentication, authorization, validation, response mapping | Starts after Silo is ready; readiness checks PostgreSQL and an Orleans ping |
| Admin | Browser administrator client | Starts after API is ready; its readiness checks the API liveness endpoint |
| Aspire Dashboard | Local OpenTelemetry view | Observability aid; it does not own application correctness |

The migration container is the only Compose startup actor that applies schema changes. API and Silo never call `Database.Migrate` during normal startup. A failed migration blocks the Silo through `service_completed_successfully`; the operator fixes the cause and reruns the same Compose command.

## 4. Configuration boundary

- Local `dotnet run` keeps localhost Orleans discovery and User Secrets.
- Containers opt into static Orleans configuration through explicit `Orleans` settings: one Silo gateway host, gateway port, silo port, advertised host, cluster ID, and service ID.
- Compose uses service DNS names (`postgres`, `redis`, `silo`, `api`) inside its private network. Published host ports bind to `127.0.0.1`.
- Database password, JWT signing key, and optional demonstration administrator password enter through environment variables. They do not enter Docker build arguments, images, tracked files, logs, or health responses.
- Application containers run as the non-root user supplied by the official .NET image.

## 5. Health contract

Each web process exposes two unauthenticated endpoints without state mutation or internal details.

- `/health/live`: proves that the process and HTTP pipeline are alive. It runs no dependency checks.
- `/health/ready`: proves that the process can serve its responsibility. API checks PostgreSQL and Orleans; Silo checks PostgreSQL; Admin checks API liveness.

Redis is intentionally excluded from Silo readiness because the accepted cache contract falls back to PostgreSQL during a Redis outage. Marking the Silo unready would contradict that existing guarantee.

## 6. Implementation slices and commit boundary

1. **Container runtime:** health checks, configurable Orleans endpoints, one-shot migrator, multi-stage Dockerfiles, `.dockerignore`, and full Compose dependencies.
2. **Verification gates:** Release build/test, style/analyzer checks, NuGet vulnerability inspection, Compose validation, image builds, and container smoke verification.
3. **Portfolio evidence:** README clean-start path, current architecture/ERD/sequence diagrams, Redis incident report, demonstration and interview scripts, and handoff status.
4. **Publication:** purpose-based commits, push, exact-commit CI check, then sync the Week 9 and roadmap state in Notion with the verified commit and remaining manual artifact.

## 7. Acceptance evidence

- `docker compose config -q` succeeds without printing resolved secrets.
- API, Silo, Admin, PostgreSQL, Redis, and the dashboard build/start under Compose; migrator exits successfully.
- Container liveness/readiness endpoints become healthy.
- A clean source snapshot follows the README path without an untracked local dependency.
- Release solution build and complete test suite pass.
- Style/analyzer and package vulnerability checks pass at the same source state.
- All application images build in CI.
- GitHub Actions succeeds at the exact public commit.
- The tracked demonstration script reproduces authentication through the representative game/reward/admin paths. The recording itself remains a separately named manual artifact until a video file or public URL exists.
