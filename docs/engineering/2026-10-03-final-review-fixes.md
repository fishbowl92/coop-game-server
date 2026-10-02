# 최종 코드 검토 잔여 항목 보완

작성일: 2026-10-03, Asia/Seoul. 구현·전체 검증은 2026-10-02에 수행했다.
기준 커밋은 `4b3dcb18a29b7b20807a9ab63f8fe3c72c8c5fa1`이며, 아래 수정은 이 보고서와 함께 게시하는 코드에 해당한다.

## 결과와 범위

이전 검토에서 남긴 재화 계산 경계, 솔로·파티 참가 경쟁, 매칭 재실행, 캐시 손상값·만료, 방 호출 제한, 관리자 화면 갱신 항목을 보완했다.
PostgreSQL 트랜잭션이 영속 변경의 성공·취소를 결정하고, Grain은 커밋 후에만 후보 상태를 채택한다.
API(Application Programming Interface, 클라이언트 요청 인터페이스)는 인증 주체와 응답 상태를 연결한다.
캐시는 원본 조회를 대체하는 보조 수단으로 유지한다.

새 데이터베이스 마이그레이션은 없다. 기존 개발 데이터베이스에는 앞선 커밋의
`20261001092734_AddDurableMatchmakingOperations`를 적용했다.

## 변경한 코드와 이유

아래 경로는 저장소 루트 기준이다. 파일 전체나 기존 공개 함수는 삭제하지 않았다.

### 캐시 손상값과 값별 최대 수명

| 파일 | 추가·변경한 함수·상태 | 이유 |
|---|---|---|
| `src/CoopGameServer.Grains/Players/Caching/RedisPlayerProgressionCache.cs` | 생성자에 선택 매개변수 `TimeProvider? timeProvider = null`, 필드 `_timeProvider` 추가 | 운영 시 시스템 시간, 테스트 시 제어 가능한 시간을 사용한다. 기존 생성자 호출도 유지된다. |
| 같은 파일 | `SchemaVersion` 1→2, `CachedPlayerProgressionPage`에 `ExpiresAtUtc` 추가, `Result`를 nullable로 변경 | 만료 정보가 없는 이전 형식은 원본으로 다시 채운다. 중첩 결과가 null인 손상값을 실제 입력으로 처리한다. |
| 같은 파일 | `ReadFirstPageAsync(playerId, pageSize)` 변경 | null 결과·누락된 만료·음수 골드·잘못된 아이템을 거절한다. 값이 만료되면 Miss를 반환한다. |
| 같은 파일 | `WriteFirstPageAsync(playerId, pageSize, result)` 변경 | 한 번 계산한 TTL(Time To Live, 자동 만료까지의 수명)을 값의 절대 만료와 Redis 키 만료에 함께 사용한다. |

Redis Hash는 여러 페이지 크기의 값을 한 키에 보관한다. 다른 크기의 쓰기가 키 만료를 연장해도
오래된 값의 `ExpiresAtUtc`는 연장되지 않는다. 만료된 값 조회 시 공유 키를 삭제하지 않아
아직 유효한 다른 크기의 값은 유지한다. 이 보완은 무효화 실패 시 값의 수명을 제한하며,
모든 쓰기 직후의 즉시 최신성을 보장하는 분산 버전 조정은 아니다.

### 보상 한도 초과와 부분 지급 방지

| 파일 | 추가·변경한 함수·상태 | 이유 |
|---|---|---|
| `src/CoopGameServer.Persistence/Rewards/RewardWriteError.cs` | `CapacityExceeded = 4` 추가 | 정수 표현 범위 초과를 명시적 업무 거부로 구분한다. 기존 열거값은 유지한다. |
| `src/CoopGameServer.GrainContracts/Players/PlayerRewardCommandError.cs` | `CapacityExceeded = 6` 추가 | 영속 계층의 거부를 Grain 호출자에게 전달한다. |
| `src/CoopGameServer.Persistence/Rewards/PostgreSqlRewardWriter.cs` | `WriteCoreAsync`의 `OverflowException` 처리, `GetTelemetryResult`의 `capacity_exceeded` 분기 추가 | 먼저 저장한 감사 기록과 골드·아이템 변경을 모두 롤백한 뒤 거부한다. 관측 결과도 구분한다. |
| `src/CoopGameServer.Persistence/Rewards/RewardWriteResult.cs` | 결과 계약 주석 변경 | 연결·시간 초과 예외와 재화 한도 거부의 차이를 반영한다. |
| `src/CoopGameServer.Grains/Players/PlayerGrain.cs` | `GrantAdminRewardAsync`, `CompleteGameAsync`의 결과 매핑 변경 | 관리자 지급·게임 결과 보상이 같은 한도 정책을 따른다. |
| `src/CoopGameServer.Api/Controllers/RewardsController.cs` | `CapacityExceeded` 응답 분기 추가 | 적용하지 않은 지급을 HTTP(Hypertext Transfer Protocol, 웹 통신 규약) 422로 반환한다. |

실제 HTTP 관리자 테스트는 골드와 아이템의 각 상한을 넘기는 요청을 두 번 호출하고,
기존 잔액·수량 유지, 실패 요청의 감사 기록 부재, 기존 성공 요청의 재생을 검사한다.
거부된 요청은 지급 영수증을 남기지 않는다. 연결 장애는 계속 예외로 전달한다.

### 참가 경쟁과 이전 결과의 재생

| 파일 | 추가·변경한 함수·상태 | 이유 |
|---|---|---|
| `src/CoopGameServer.Grains/Persistence/PlayerParticipationGuard.cs` **추가** | `LockAsync(GameDbContext context, Guid playerId)` | 호출자의 트랜잭션에서 같은 Player 행을 `FOR UPDATE`로 잠근다. 다른 파티·대기열 Grain도 같은 참가 경계를 공유한다. |
| 같은 파일 | `HasActiveSoloTicketAsync(GameDbContext context, Guid playerId)` | Queued·Matched 상태의 솔로 티켓을 영속 원본에서 확인한다. |
| `src/CoopGameServer.Grains/Parties/PartyGrain.cs` | `ExecuteCommandAsync`의 Create·Join 처리 변경 | 잠금 후 솔로 참가를 재조회한다. 경쟁에서 패한 후보를 버리고 거부 결과를 저장한다. |
| `src/CoopGameServer.Grains/Matchmaking/MatchQueueGrain.cs` | `ExecuteCommandAsync`에 선택 매개변수 `MatchQueueEntryRequest? enqueueRequest = null` 추가, `EnqueueAsync`에서 전달 | 새 솔로 등록에만 참가 잠금을 적용한다. 플레이어 존재·파티 소속·다른 대기열의 활성 솔로 티켓을 검사한다. |
| 같은 파일 | `ExecuteCommandAsync`에서 `IsReplay` 조기 반환 | 이미 확정된 요청을 현재 파티 소속으로 재판정하거나 대기열 전체를 다시 저장하지 않는다. |
| `src/CoopGameServer.Grains/Matchmaking/MatchQueueState.cs` | `RejectEnqueue(MatchQueueEntryRequest request, MatchQueueCommandError error)` 추가 | 경쟁 검사에서 실패한 요청의 결과만 저장하고 티켓·배정을 만들지 않는다. |
| `src/CoopGameServer.GrainContracts/Parties/PartyCommandError.cs` | `PlayerAlreadyInMatchmaking = 22` 추가 | 활성 솔로 참가 중 파티 가입 거부를 구분한다. |
| `src/CoopGameServer.GrainContracts/Matchmaking/MatchQueueCommandError.cs` | `SoloPlayerAlreadyInParty = 17`, `PlayerNotFound = 18` 추가 | 참가 원본 검사의 실패 원인을 구분한다. |
| `src/CoopGameServer.Api/Application/Matchmaking/MatchmakingService.cs` | 생성자의 `GameDbContext` 매개변수·의존성 **제거**, `EnqueueSoloAsync` 사전 DB 조회 **제거** | 검사와 등록 사이의 경쟁을 Grain의 트랜잭션에서 해결한다. 취소 후 파티에 가입한 플레이어도 예전 성공 요청을 재생할 수 있다. 호출 전 취소 검사와 호출 대기 취소는 유지한다. |

취소된 티켓의 예전 등록 요청을 재생해도 티켓을 다시 Queued로 만들지 않는다.
새 등록 요청은 현재 파티 소속에 따라 거부된다. 이는 Silo 재시작을 포함한 통합 테스트로 확인했다.
클라이언트가 대기를 취소해도 이미 수락된 영속 명령을 되돌리는 계약은 추가하지 않았다.

### 방 호출 제한과 관리자 화면

| 파일 | 추가·변경한 함수·상태 | 이유 |
|---|---|---|
| `src/CoopGameServer.Api/Authentication/GameRoomRateLimits.cs` | `Add`의 분할 키를 인증된 Player GUID로 변경 | 방 ID·GUID 문자열 표기·연결 ID를 바꿔 제한을 우회하지 못하게 한다. |
| 같은 파일 | `AddGameRoomRateLimits`에 `room-command`, `room-read` 각각 초당 30회 추가 | 일반 명령과 상태 조회에도 독립 예산을 적용한다. 기존 heartbeat 초당 2회·reconnect 분당 5회는 유지한다. |
| `src/CoopGameServer.Api/Controllers/GameRoomPlayController.cs` | `GetState`에 조회 정책, `Connect`·`Disconnect`·`StartCombat`·`Attack`·`Skill`에 명령 정책 추가 | 실제 HTTP 진입 경로에 정책을 연결한다. 연결 자격 검사는 계속 Grain에서 수행한다. |
| `src/CoopGameServer.Admin/Services/AdminOperationsState.cs` | `RefreshAsync`, `GrantAsync`의 지급 후 갱신 변경 | 선택한 Player ID로 프로필도 다시 조회하여 변경 전 닉네임을 재사용하지 않는다. |
| 같은 파일 | `IsDefiniteRejection`에 HTTP 422 추가 | 최초 한도 거부 시 입력 수정을 허용한다. 응답을 잃은 요청의 재시도 식별자는 계속 유지한다. |

호출 제한은 API 프로세스 하나의 메모리 정책이다. 여러 API에 공통으로 적용되는 분산 제한은 아니다.

## 테스트 변경

| 파일 | 추가·변경한 함수·매개변수 | 검증 목적 |
|---|---|---|
| `tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/PlayerParticipationTests.cs` **추가** | `SoloEnqueueRacingPartyCreateOrJoinHasOnlyOneWinner(bool join)` | 생성·가입과 솔로 등록을 각각 8회 경쟁시켜 한쪽만 성공하고 DB가 일치하는지 검사한다. |
| 같은 파일 | `OldEnqueueReplaysAfterCancellationPartyJoinAndSiloRestart`, `ConcurrentSoloEnqueuesAcrossQueuesCannotOccupyTwoTickets`, 보조 함수 `Solo(Guid player)` | 이전 결과 재생과 서로 다른 대기열의 중복 참가 방지를 검사한다. |
| `tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/MatchQueueGrainTests.cs` | `CreateSoloEntry`를 비동기 `CreateSoloEntryAsync`로 변경, 호출부 변경 | 실제 존재하는 플레이어를 등록하여 강화된 원본 검사를 만족시킨다. |
| `tests/CoopGameServer.IntegrationTests/Controllers/MatchmakingFlowControllerTests.cs`, `tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/DurableRecoveryTests.cs` | `MatchmakingService` 생성자 호출 변경 | 제거한 DB 의존성에 맞춘다. |
| `tests/CoopGameServer.IntegrationTests/Grains/Players/PlayerGrainTests.cs` | `CorruptFirstPageIsDiscardedAndFilledAgainFromPostgreSql(bool nestedNull)`로 이론 테스트 변경 | 잘못된 JSON과 중첩 null 결과를 각각 원본 조회로 복구한다. |
| `tests/CoopGameServer.IntegrationTests/Grains/Players/RedisPlayerProgressionCacheTests.cs` | fixture 생성자 매개변수, `RefreshingAnotherPageSizeDoesNotExtendExpiredPayload` 추가 | 실제 Redis 키가 살아 있어도 오래된 값은 Miss이고 새 값은 Hit임을 확인한다. |
| `tests/CoopGameServer.IntegrationTests/Controllers/AdminOperationsHttpTests.cs` | `CapacityExceededReturns422AndRollsBackBothAuditsAndBalances(bool goldOverflow)` 추가 | 실제 인증 HTTP 요청의 골드·아이템 초과와 전체 롤백을 확인한다. |
| `tests/CoopGameServer.IntegrationTests/Persistence/Rewards/PostgreSqlRewardWriterIntegrationTests.cs` | 기존 골드 한도 테스트의 기대 결과 변경 | 예외 대신 CapacityExceeded 거부와 원본 보존을 확인한다. |
| `tests/CoopGameServer.IntegrationTests/Controllers/GameRoomPlayHttpTests.cs` | `ChangingRoomIdsCannotBypassPlayerRateBudget` 추가 | 방·GUID 표기 변경에도 12회 중 7회가 429이며 다른 플레이어의 예산은 독립적이다. |
| `tests/CoopGameServer.UnitTests/Admin/AdminOperationsStateTests.cs` | `FirstDefiniteRejectionAllowsCorrection`의 422 사례, `RefreshReplacesRenamedProfileWithoutChangingTarget` 추가, `Scenario.Read` 조회 ID 처리 변경 | 한도 거부 후 입력 수정과 같은 플레이어의 새 닉네임 표시를 확인한다. |

## 실행 근거

아래 애플리케이션 검증은 2026-10-02의 동일한 수정 코드·Release 구성으로 수행했다.
2026-10-03에는 저장소 상태·TRX 결과 파일·실행 중 서비스·준비 상태를 재확인했다.
문서만 정리했으므로 애플리케이션 검증을 중복 실행하지 않았다.

| 명령·검사 | 결과와 범위 |
|---|---|
| `dotnet format style CoopGameServer.slnx --verify-no-changes --no-restore --exclude src/CoopGameServer.Persistence/Migrations` | 성공. 자동 생성 마이그레이션 제외. |
| `dotnet format analyzers CoopGameServer.slnx --verify-no-changes --no-restore` | 성공. |
| `dotnet build CoopGameServer.slnx --configuration Release --no-restore` | 성공, 경고·오류 0. 전체 솔루션. |
| `dotnet test CoopGameServer.slnx --configuration Release --no-build --logger 'trx;LogFilePrefix=final-review' --results-directory artifacts/verification-20261002/tests` | 단위 143개·통합 158개, 총 301개 통과. 실패·건너뜀 0. |
| `docker compose --profile app build` | API·Silo·Admin·Migrator 네 이미지 빌드 성공. |
| `docker compose --profile app up -d --wait --wait-timeout 120` | 준비 대기 성공. Migrator 정상 종료, 나머지 서비스 준비 완료. |
| 대표 HTTP 데모 | 플레이어 4명, 파티·매칭·방 접속·첫 전투 행동, 관리자 조회·골드 250 지급·이력 확인 성공. 전체 게임 완료 시연은 아니다. |
| 2026-10-03 `/health/ready` 재확인 | API 5265·Silo 5266·Admin 5275 모두 HTTP 200·Healthy. |

로컬 TRX 두 파일과 데모 출력은 `artifacts/verification-20261002/`에 있다.
Git에 포함하지 않는 실행 자료이므로 공개 자동 검증은 해당 게시 커밋의 GitHub Actions를 확인한다.
명령의 `--no-build`는 앞서 빌드한 동일한 코드·구성을 재사용한다는 뜻이다.

기존 PostgreSQL·Redis 볼륨을 유지했다. 마이그레이션 전 PostgreSQL 백업은
`artifacts/verification-20261002/before-migration.dump`에 보관했다.
기존 개발 서명 키와 새 로컬 관리자 설정은 Git에서 제외된 `.env`로만 전달했고 비밀값은 출력하지 않았다.

## 남은 범위

- 이번 검토에서 지정한 코드 보완 항목과 요구한 로컬 검증은 완료했다. 이 판정은 향후 모든 결함이 없다는 의미가 아니다.
- 3~5분 영상 파일·공개 URL은 아직 없다. [촬영 계획](../portfolio/demo-and-interview-script.md)에 따라 실제 촬영이 필요하다.
- 여러 Silo의 운영 구성, 공개 서버의 TLS(Transport Layer Security, 통신 암호화), 외부 비밀 관리, 운영 백업·복구, 지속 부하는 현재 단일 PC 포트폴리오의 검증 범위 밖이다.
- [8주차 부하 기준선](../performance/week-08/README.md)은 2026-09-23 코드의 측정이다. 이번 코드의 성능 수치나 최적화·After 결과로 인용하지 않는다.

## 게시 결과 — 2026-10-03

- 코드·테스트·보고서: [`fa5b4ef`](https://github.com/fishbowl92/coop-game-server/commit/fa5b4ef2fee06b4b24cad0ad18b1577fe78dcac6), 커밋·`main` 푸시 완료.
- 같은 코드 커밋의 [CI(Continuous Integration, 지속적 통합) 37039655073](https://github.com/fishbowl92/coop-game-server/actions/runs/37039655073): 복원·스타일·분석기·취약성 검사·Release 빌드·전체 테스트·네 이미지 빌드 모두 성공.
- Notion: 대시보드·로드맵·캐시·관측성·공개 포트폴리오 다섯 페이지의 상태·근거를 수정하고 다시 조회했다. 수정 내용과 하위 페이지·데이터베이스 참조 보존을 확인했다.
- 이후 보고서·[작업 인계](../work/current.md)를 정리하는 문서 커밋에는 애플리케이션·테스트 변경이 없다. 현재 원격 HEAD의 자동 검증 상태는 재개 시 다시 확인한다.
