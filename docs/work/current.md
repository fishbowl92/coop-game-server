# Current work handoff

Updated: 2026-09-23 (Asia/Seoul). Snapshot, not live status.
Follow [AGENTS.md](../../AGENTS.md); recheck volatile facts.

## Active work

- Week 7 OpenTelemetry observability is implemented, locally validated, committed, pushed, and verified by remote CI.
- The implementation contract, changed symbols, validation evidence, and limits are in [Week 7 observability](../design/week-07-observability.md).
- Local startup and Dashboard usage are in [local development](../runbooks/local-development.md).
- The user deferred all learning-center and Notion updates until the remaining weeks are complete. Do not synchronize them as part of ordinary Week 7 follow-up.

## Current implementation

- `CoopGameServer.Observability` gives API and Silo one OpenTelemetry resource, trace, metric, log, and optional OTLP export policy.
- API HTTP Activities propagate W3C Trace Context through the Orleans Client and Silo to custom PostgreSQL reward and Redis cache Activities.
- Npgsql instrumentation uses a named `GameDb` data source so the metric pool name is not the connection string.
- Reward persistence records bounded operation and result tags for applied, replayed, defined business errors, and exceptions. Request and Player IDs remain trace-only correlation fields.
- Redis read, write, and invalidation Activities omit the Redis key, cached JSON, and Player ID. Redis failures retain the existing PostgreSQL fallback and post-commit success behavior.
- ASP.NET Core automatic exception detail recording is disabled. Custom failure Activities record the exception type without its message.
- Development API and Silo export OTLP/gRPC to `http://localhost:4317` by default. The endpoint can be overridden with `OTEL_EXPORTER_OTLP_ENDPOINT` and is optional outside Development.
- Compose starts Aspire Dashboard `13.5.2` on loopback-only UI and OTLP ports. The local startup script accepts `healthy` for PostgreSQL and Redis and `running` for the Dashboard.
- API and Silo content roots point to their build output, so root-level `dotnet run --project ...` commands load the correct project settings.

## Verification and delivery

- Release solution build passed with 0 warnings and 0 errors.
- Complete regression passed: 141 unit tests and 139 integration tests, 280 total with no failures or skips.
- Focused checks cover bounded metric tags, exception-message exclusion, the `GameDb` pool name, HTTP-to-Orleans-to-PostgreSQL Trace ID propagation, and Redis outage Activities without Player IDs or keys.
- `docker compose config -q`, the full local startup script, Dashboard HTTP 200, and actual OTLP ingestion from Development API and Silo passed.
- Implementation commit: `1779dbb` (`OpenTelemetry 관측성과 로컬 Dashboard 연결`).
- Test commit: `7a4197c` (`관측성 전파와 비밀 비노출 검증 추가`).
- Design and runbook commit: `7ab226a` (`7주차 관측성 설계와 실행 절차 정리`).
- All three commits are pushed to `origin/main`.
- GitHub Actions CI run `35751083308` passed at `7ab226a` on 2026-09-23. It verifies the configured restore, build, and test workflow at that commit.

## Workspace note

- The pre-existing untracked `CoopGameServer/` review-data directory is preserved and excluded from staging.
- PostgreSQL, Redis, and Aspire Dashboard containers are running as the local development environment. API and Silo smoke-test processes were stopped and ports 5265 and 30000 were verified free.
- The temporary local account and Player created for OTLP ingestion verification were deleted by exact Player ID after the smoke test.
- The full-solution format baseline still reports pre-existing line-ending, encoding, and unrelated import issues. A format check restricted to every Week 7 changed C# file passes.

## Outstanding boundaries

- Aspire Dashboard is an in-memory local development viewer. Durable telemetry storage, retention, production authentication, TLS, and external exposure are not implemented.
- Alert delivery, on-call procedures, production sampling policy, and SLO(Service Level Objective, 서비스 수준 목표) thresholds remain unimplemented.
- Redis uses application-level cache Activities rather than low-level command instrumentation so keys cannot leak into telemetry.
- Existing learning-center pages remain based on an earlier source snapshot and need a final code-backed review after all weeks are complete.

## Next action

Start Week 8 load testing from the Week 7 measurements: define reproducible API, Orleans, PostgreSQL, and Redis scenarios and performance targets first, capture a baseline, then optimize only measured bottlenecks.
