# Portfolio demo and interview script

Updated: 2026-09-29 (Asia/Seoul)

## 3-5 minute screen recording plan

| Time | Screen | Explanation |
| --- | --- | --- |
| 0:00-0:30 | README and system diagram | “ASP.NET Core API, Orleans Silo, PostgreSQL, Redis, Blazor Admin을 분리했습니다. PostgreSQL이 내구성 있는 정답이고 Redis는 진행도 읽기 캐시입니다.” |
| 0:30-1:10 | `docker compose --profile app up -d --build` and `docker compose ps` | “Migrator가 먼저 스키마를 적용하고 종료 코드 0일 때만 Silo, API, Admin이 순서대로 시작합니다. 앱 컨테이너는 비루트 사용자로 실행됩니다.” |
| 1:10-2:20 | `Invoke-PortfolioDemo.ps1` output | “네 사용자의 JWT 신원을 각각 검증하고 4인 파티를 매칭합니다. 연결 ID와 세대를 서버가 발급하며 전투 명령은 시퀀스와 상태 버전을 함께 검사합니다.” |
| 2:20-3:10 | Admin UI and reward history | “관리자만 보상을 지급할 수 있고 관리자 Account ID, Request ID, 보상 영수증을 PostgreSQL에 함께 남깁니다. 같은 Request ID 재시도는 새 효과를 만들지 않습니다.” |
| 3:10-4:00 | Redis stop and health endpoints | “Redis를 중단해도 Silo와 API 준비 상태가 유지됩니다. 진행도는 PostgreSQL로 폴백하며 Redis를 Exactly-once 증거로 사용하지 않습니다.” |
| 4:00-4:30 | GitHub Actions | “같은 공개 커밋에서 복원, 스타일, 분석기, 취약성, Release 빌드, 전체 테스트, 네 이미지 빌드를 확인합니다.” |

Recording checklist:

- Hide `.env`, JWT, passwords, connection strings, and browser developer storage.
- Show the commit SHA and the matching successful GitHub Actions run.
- State that the Compose package is a single-machine portfolio environment.
- State test and load numbers with their exact scenario; do not present them as production capacity.

## 3-minute interview answer

이 프로젝트는 협동 게임 서버에서 재시도, 동시성, 재시작이 데이터 중복이나 손실로 이어지지 않게 만드는 데 초점을 둔 개인 학습 프로젝트입니다.

HTTP 경계는 ASP.NET Core가 JWT 인증과 본인·관리자 인가를 담당하고, Orleans Grain이 Player·Party·MatchQueue·GameRoom 단위의 명령 순서를 조정합니다. 다만 Grain 직렬 실행만으로 데이터베이스의 모든 쓰기 경로가 보호되지는 않으므로, 최종 일관성은 PostgreSQL 트랜잭션, 고유 제약, 행 잠금, 영속 Request ID 영수증으로 보장했습니다.

Redis는 Player 진행도 읽기에만 Cache-Aside로 사용합니다. Redis 장애 시 PostgreSQL로 폴백하며 보상 중복 방지의 근거로 사용하지 않습니다. 게임 방 연결에는 서버가 발급한 Connection ID와 Generation을 넣어 이전 연결의 늦은 패킷을 거부합니다.

검증은 순수 규칙 단위 테스트, 실제 PostgreSQL·Redis·Orleans 통합 테스트, JWT를 통과하는 HTTP 테스트, 고정 시나리오 부하 실험, 전체 Compose 스모크로 나눴습니다. CI에서는 복원, 스타일, 분석기, 패키지 취약성, Release 빌드, 전체 테스트와 컨테이너 이미지 빌드를 같은 커밋에서 확인합니다.

현재 범위는 단일 Silo와 로컬 Compose입니다. 실시간 소켓 전송, 다중 Silo 멤버십, 운영 TLS·비밀 관리자·백업은 구현 범위로 주장하지 않습니다.

## 10-minute interview outline

1. **Problem and boundary (1 minute)**
   - Four-player cooperative flow: account, party, matchmaking, room, combat, reward, administration.
   - API authenticates; Grain orchestrates; PostgreSQL proves durable effects; Redis accelerates reads.
2. **Idempotency and concurrency (2 minutes)**
   - Separate duplicate retry from distinct concurrent commands.
   - Persist Request ID, payload identity, and result receipt.
   - Use one PostgreSQL transaction and player row locking for wallet/inventory/reward audit.
3. **Room state and connection safety (2 minutes)**
   - Persist room state before adopting in-memory candidate state.
   - Validate command sequence and known state version.
   - Validate server-issued Connection ID plus Generation to reject stale sessions.
4. **Recovery (1.5 minutes)**
   - Keep finalization pending state and retry metadata durable.
   - Recovery worker finds due rooms and reuses the same idempotent completion path.
   - Single-Silo limitation and future distributed lease.
5. **Cache failure (1 minute)**
   - Cache-Aside progression reads; short timeout and fail-fast backlog.
   - Redis outage falls back to PostgreSQL; readiness remains healthy by design.
6. **Evidence (1.5 minutes)**
   - Unit vs database integration vs HTTP/JWT tests prove different boundaries.
   - Week 8 load figures are tied to offered load, duration, valid responses, and post-run invariants.
   - Week 9 Compose and CI connect existing guarantees to a clean source run.
7. **Limits and next work (1 minute)**
   - No production capacity claim, multi-region guarantee, or real-time transport claim.
   - Next evidence should target multi-Silo recovery coordination and production-like observability thresholds.

## Artifact status

The executable demo script and recording plan are tracked. A 3-5 minute video is complete only when a video file or public URL is recorded and reviewed for secret exposure.
