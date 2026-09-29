# Redis outage exercise report

- Date: 2026-09-29 (Asia/Seoul)
- Environment: local Docker Desktop, Week 9 Compose package
- Classification: controlled failure exercise, not a production incident

## Purpose

Player progression uses Redis as a Cache-Aside(캐시에서 먼저 찾고 없으면 원본을 읽는 방식) optimization. The exercise checks the accepted boundary: Redis failure may increase PostgreSQL reads and latency, but must not make PostgreSQL-backed game correctness unavailable.

## Procedure and evidence

1. Started the full Compose stack and confirmed API, Silo, and Admin readiness were `Healthy`.
2. Stopped only the `redis` service with `docker compose stop redis`.
3. Waited eight seconds, longer than the container health interval and the configured 100 ms cache operation timeout.
4. Called `http://localhost:5266/health/ready` and `http://localhost:5265/health/ready`.
5. Both returned `Healthy` while Redis was stopped.
6. Restarted Redis and waited until its container health became `healthy`.

The automated integration test `PlayerGrainTests.RedisOutageFallsBackToPostgreSqlAndDoesNotFailProgression` separately executes a progression read while Redis is unavailable and checks the PostgreSQL fallback. The Compose exercise proves process/readiness behavior; the integration test proves the application fallback result at its test scope.

## Impact and response

- Durable game and reward state remained owned by PostgreSQL.
- Silo readiness intentionally excluded Redis, so Docker did not restart or remove a server that could still serve canonical data.
- API readiness remained healthy because its PostgreSQL and Orleans paths stayed available.
- Redis was restarted without clearing its named volume. Cache content may be stale or absent after an outage, so normal expiry and write-through invalidation rules still apply.

## Detection and limitations

The current evidence uses container health, application health endpoints, integration tests, and OpenTelemetry-ready cache metrics. It does not prove a production alert route, a Redis hit ratio during the outage, sustained latency under simultaneous load, or behavior across multiple Silo nodes. Those require an external metrics backend, alert thresholds, and a fixed load scenario.

## Follow-up decisions

- Keep Redis outside Silo readiness while PostgreSQL fallback remains the accepted behavior.
- Keep cache operations fail-fast and shorter than the request budget.
- Treat any future Redis use for sessions, distributed rate limits, or coordination as a separate availability contract; this report does not authorize those uses.
- Add an alert only after a production-like backend can measure fallback rate and latency with an explicit threshold.
