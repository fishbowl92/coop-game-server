# 5주차: Redis 플레이어 진행도 Cache-Aside

- 구현일: 2026-09-16
- 대상: 서버 프로그래밍과 분산 캐시를 처음 학습하는 개발자
- 관련 결정: [PostgreSQL·Redis 책임 분리](../adr/0002-separate-postgresql-and-redis-responsibilities.md)
- 이전 조회 경계: [PlayerGrain 보상 경계](./week-04-01-player-grain-reward-boundary.md#13-조회-정책)

## 1. 이번 작업의 목적

플레이어 화면을 열 때 필요한 프로필, 골드, 인벤토리 첫 페이지를 하나의 인증된 HTTP API로 제공합니다. 자주 반복되는 첫 페이지 조회는 Redis(REmote DIctionary Server, 원격 딕셔너리 서버)에 잠시 저장하고 재사용합니다.

PostgreSQL은 계속 Source of Truth(데이터의 공식 원본)입니다. Redis가 비어 있거나 느리거나 중지되어도 영구 데이터의 정확성과 조회 가능 여부는 PostgreSQL이 결정합니다. 캐시는 성능을 위한 복사본일 뿐 보상 중복 방지나 잔액의 최종 근거가 아닙니다.

## 2. 공개 API

```http
GET /api/players/{playerId}/progression?pageSize=20&continuationToken=...
Authorization: Bearer <JWT>
```

응답에는 다음 값이 포함됩니다.

- `playerId`, `nickname`, `createdAt`, `updatedAt`
- `gold`
- Item ID 오름차순의 `items`
- 다음 페이지가 있을 때의 `nextContinuationToken`

일반 Player는 JWT(JSON Web Token, 서명된 로그인 토큰)의 Player ID와 URL의 `playerId`가 같을 때만 조회할 수 있습니다. 관리자는 기존 인가 정책에 따라 다른 Player도 조회할 수 있습니다. `pageSize`는 1~100이고 잘못된 연속 토큰은 HTTP 400으로 변환됩니다.

## 3. 책임 경계

| 계층 | 책임 |
| --- | --- |
| `PlayersController` | JWT 인증 이후 본인/관리자 인가, Query String을 Grain 계약으로 변환, HTTP 상태와 응답 DTO 변환 |
| `IPlayerGrainClient` | API가 큰 `IGrainFactory`에 직접 의존하지 않게 PlayerGrain 호출을 감쌈 |
| `PlayerGrain` | 같은 Player의 조회·보상·캐시 무효화 순서를 조정하고 Cache-Aside 흐름을 실행 |
| `IPlayerProgressionCache` | Grain과 StackExchange.Redis 구현을 분리 |
| `RedisPlayerProgressionCache` | Redis 직렬화, TTL, timeout, 손상 검증, 오류 흡수 |
| `GameDbContext` | Repeatable Read(반복 읽기 격리 수준) Transaction 안에서 프로필·골드·인벤토리 원본 조회 |
| PostgreSQL | 플레이어, 지갑, 인벤토리, 보상 감사 기록의 영구 원본 |
| Redis | 진행도 첫 페이지의 삭제 가능한 단기 복사본 |

Domain(도메인) 프로젝트에는 Redis 타입을 추가하지 않았습니다. 게임 규칙과 외부 캐시 기술을 분리하기 위해서입니다.

## 4. Cache-Aside 조회 순서

```text
인증된 GET 요청
  -> PlayerGrain.GetProgressionPageAsync
     -> 첫 페이지인가?
        -> Redis Hash 조회
           -> Hit: 캐시 결과 반환
           -> Miss/Error/Corrupt: PostgreSQL로 진행
        -> 프로필 + 지갑 + 인벤토리를 한 DB Snapshot으로 조회
        -> 첫 페이지 성공 결과를 Redis에 저장
        -> 결과 반환
     -> 다음 페이지이면 PostgreSQL에서 직접 조회
```

Cache-Aside(캐시 우선 조회 후 없으면 원본 조회)는 캐시에 값이 없을 때 애플리케이션이 원본을 읽고 캐시를 채우는 방식입니다. 이 구현은 첫 페이지에만 적용합니다. 인벤토리 전체를 한 키에 넣거나 모든 연속 페이지 조합을 저장하면 키 수와 무효화 비용이 커지기 때문입니다.

Player가 없거나 입력이 잘못된 오류 결과는 저장하지 않습니다. 일시적인 오류를 캐시하면 Player 생성 직후에도 과거의 404가 남을 수 있습니다.

## 5. Redis 키와 값

한 Player당 하나의 Hash Key를 사용합니다.

```text
Key   = coopgame:player-progression:v1:{playerId를 하이픈 없이 표시}
Field = first:{pageSize}
Value = schemaVersion, playerId, pageSize, progression result를 담은 JSON
```

예를 들어 같은 Player가 `pageSize=20`과 `pageSize=50`을 요청하면 한 Hash 아래 `first:20`, `first:50` 두 필드가 생깁니다. Player 데이터가 바뀌면 Key 하나를 `DEL`하여 모든 PageSize 변형을 함께 제거합니다.

`v1`과 `schemaVersion=1`은 코드 배포 뒤 JSON 구조가 바뀌었을 때 구 형식과 신 형식을 구분합니다. 역직렬화한 값의 버전, Player ID, PageSize와 성공 상태가 기대와 다르면 손상된 값으로 보고 Key를 삭제한 뒤 DB를 조회합니다.

## 6. TTL, Jitter와 timeout

운영 초기값은 다음과 같습니다.

| 설정 | 값 | 이유 |
| --- | ---: | --- |
| `EntryTtl` | 2분 | 무효화 실패가 계속되어도 오래된 값의 최대 생존 시간을 제한 |
| `MaxJitter` | 20초 | 많은 Player Key가 같은 순간 만료되어 DB 부하가 몰리는 현상을 완화 |
| `OperationTimeout` | 100ms | 느린 캐시를 오래 기다리지 않고 PostgreSQL 대체 경로로 전환 |

Jitter(지터, 만료 시각에 더하는 작은 무작위 시간)는 0~20초입니다. 실제 TTL은 2분 이상 2분 20초 미만입니다.

StackExchange.Redis 연결은 `AbortOnConnectFail=false`로 설정해 Silo 시작 순간 Redis가 꺼져 있어도 프로세스가 시작될 수 있게 했습니다. `BacklogPolicy.FailFast`는 연결이 끊겼을 때 명령을 쌓아 두었다가 나중에 실행하지 않게 합니다. 특히 오래된 캐시 쓰기가 Redis 복구 뒤 늦게 반영되는 일을 막습니다.

## 7. 데이터 변경과 무효화

### 보상 지급

```text
PlayerGrain
  -> PostgreSQL 보상 Transaction 성공
  -> Redis Player Key 삭제 시도
  -> 보상 결과 반환
```

동일 요청의 Replay(재생 응답)에서도 삭제를 다시 시도합니다. 이전 호출이 DB Commit 뒤 Redis 삭제 전에 중단됐을 가능성을 복구하기 위해서입니다.

### 닉네임 변경

```text
PlayersController
  -> PostgreSQL 닉네임 UPDATE 성공
  -> 같은 PlayerGrain의 InvalidateProgressionCacheAsync 호출
  -> HTTP 결과 반환
```

캐시 조회와 무효화가 같은 PlayerGrain 큐를 통과하므로, 먼저 시작한 조회가 오래된 Snapshot을 뒤늦게 캐시에 채워도 뒤에 대기한 무효화가 다시 삭제합니다. DB Commit과 무효화 호출 사이에 이미 진행 중인 요청 하나는 과거 값을 받을 수 있습니다. 닉네임 변경 HTTP 응답이 완료되기 전에 무효화 호출까지 기다려 이후 요청의 창을 닫습니다.

Redis 삭제 실패는 성공한 PostgreSQL 변경을 되돌리지 않습니다. 이때 오래된 값은 TTL이 끝날 때까지 남을 수 있으므로 로그와 `redis_errors{operation="invalidate"}` 지표로 관찰합니다.

## 8. 장애 순서와 보장

| 상황 | 동작 | 보장 |
| --- | --- | --- |
| Cache Hit | Redis 값 반환 | DB 조회 감소 |
| Cache Miss | PostgreSQL 조회 후 Redis 저장 시도 | 원본 결과 반환 |
| Redis 읽기 오류/timeout | PostgreSQL 조회 | 조회 기능 유지 |
| JSON 손상/계약 불일치 | Key 삭제 후 PostgreSQL 조회 | 손상 값 외부 노출 방지 |
| Redis 쓰기 실패 | DB에서 읽은 결과 반환 | 조회 성공 유지 |
| Redis 무효화 실패 | DB 변경 성공을 유지 | TTL 안의 제한된 Stale(오래된 값) 가능 |
| PostgreSQL 실패 | 요청 실패 | Redis가 원본 장애를 성공으로 위장하지 않음 |

Redis 장애 대체 조회는 Availability(가용성)를 높이지만 PostgreSQL 장애까지 감추지는 않습니다. 원본이 실패한 상태에서 오래된 캐시를 무조건 반환하면 재화 화면이 정확한 것처럼 보일 수 있어 현재 범위에서는 허용하지 않았습니다.

## 9. 측정 지표

`System.Diagnostics.Metrics`의 Meter 이름은 `CoopGameServer.PlayerProgressionCache`입니다.

- `coopgame.player_progression_cache.requests{result=hit|miss|error|corrupt}`
- `coopgame.player_progression_cache.fallbacks{reason=miss|error|corrupt}`
- `coopgame.player_progression_cache.redis_errors{operation=read|write|invalidate|delete_corrupt}`
- `coopgame.player_progression_cache.redis_duration{operation=...}` 단위 ms
- `coopgame.player_progression_cache.database_fill_duration` 단위 ms

Player ID를 태그로 넣지 않았습니다. Player마다 새 시계열이 생기는 High Cardinality(높은 카디널리티) 문제를 막기 위해서입니다. Dashboard(대시보드)와 외부 Exporter(지표 전송기)는 7주차 운영 관측 범위입니다.

## 10. 구현 파일과 함수

### 생성

- `Grains/Players/Caching/PlayerProgressionCacheOptions.cs`
  - `KeyPrefix`, `EntryTtl`, `MaxJitter`, `OperationTimeout`: 캐시 수명과 장애 전환 한도
  - `Validate()`: 잘못된 시작 설정 거부
- `IPlayerProgressionCache.cs`
  - `ReadFirstPageAsync(playerId, pageSize)`
  - `WriteFirstPageAsync(playerId, pageSize, result)`
  - `InvalidateAsync(playerId)`
- `RedisPlayerProgressionCache.cs`: 실제 Redis Hash·JSON·TTL·오류 처리
- `PlayerProgressionCacheReadResult.cs`: Hit/Miss/Error/Corrupt를 값과 분리
- `PlayerProgressionCacheMetrics.cs`: Counter(누적 횟수)와 Histogram(분포) 기록
- `Contracts/Players/PlayerProgressionResponse.cs`, `PlayerInventoryItemResponse.cs`: 외부 HTTP 응답
- `Silo/appsettings.json`: 로컬 Redis 주소와 초기 캐시 설정
- `PlayerProgressionHttpTests.cs`: 실제 JWT·HTTP·Grain·DB·Redis 연결과 닉네임 변경 뒤 캐시 삭제 검증
- `PlayerProgressionCacheMetricsTests.cs`: 지표 이름과 태그 계약 검증

### 변경

- `IPlayerGrain.GetProgressionPageAsync(query)`: 반환값에 프로필 필드를 추가하되 기존 Orleans 필드 ID 0~3 유지
- `IPlayerGrain.InvalidateProgressionCacheAsync()`: API의 DB 변경을 같은 PlayerGrain 큐 뒤에서 무효화
- `PlayerGrain.GetProgressionPageAsync(query)`: 첫 페이지 Cache-Aside와 DB fallback 추가
- `PlayerGrain.GrantAdminRewardAsync(command)`, `CompleteGameAsync(command)`: DB 성공/Replay 뒤 캐시 삭제
- `PlayersController.GetPlayerProgression(playerId, pageSize, continuationToken, cancellationToken)`: 인증된 결합 조회 API
- `PlayersController.UpdatePlayerNickname(...)`: DB Commit 뒤 캐시 삭제 요청
- `OrleansTestClusterFixture`: 실제 Redis Testcontainer 등록
- `PlayerGrainTests`: 캐시 저장·적중·명시적/보상 무효화·TTL·손상값 복구·Redis 장애 중 조회와 보상 재생·연속 페이지 지표 제외 검증

삭제한 공개 함수나 데이터베이스 열은 없습니다. 새 DB Migration(마이그레이션)도 없습니다.

## 11. 테스트가 증명하는 범위

- 계약 단위 테스트: Orleans 직렬화 ID와 공개 메서드 형태
- 지표 단위 테스트: 계측 이름, 결과/작업 태그, Player ID 태그 부재
- PlayerGrain 통합 테스트: 실제 PostgreSQL과 실제 Redis에서 Cache Hit, TTL, 무효화, 손상 JSON 폐기와 재채움
- 장애 통합 테스트: Redis가 연결될 수 없는 별도 TestCluster에서도 PostgreSQL 조회, 보상 지급, 동일 요청 Replay 유지
- HTTP 통합 테스트: 토큰 없음 401, 다른 Player 403, 본인 200, 입력 오류 400, 닉네임 변경 뒤 실제 Redis Key 삭제
- 지표 통합 테스트: 캐시 대상인 첫 페이지 DB 조회만 `database_fill_duration`에 기록하고 연속 페이지는 제외

직접 Controller를 호출하는 단위 테스트만으로는 JWT Middleware(미들웨어)를 증명할 수 없습니다. 그래서 `WebApplicationFactory`로 실제 ASP.NET Core 요청 파이프라인을 통과하는 테스트를 추가했습니다.

## 12. 2026-09-16 검증 결과

- `dotnet build CoopGameServer.slnx --configuration Release --no-incremental`: 경고 0개, 오류 0개
- `dotnet test CoopGameServer.slnx --configuration Release --no-build`: 단위 110개, 통합 130개, 총 240개 통과
- 통합 테스트는 Testcontainers가 만든 PostgreSQL과 Redis를 사용합니다.
- 별도 Orleans TestCluster에 닫힌 Redis 포트를 주입해 조회 Fallback, 보상 지급, 같은 요청 Replay를 확인했습니다.
- 손상 JSON 복구, 닉네임 변경 HTTP 뒤 실제 Redis Key 삭제, 연속 페이지의 DB 채움 지표 제외를 각각 확인했습니다.

전체 테스트 통과는 현재 로컬 커밋의 코드 동작을 증명합니다. 원격 Push와 GitHub Actions CI(Continuous Integration, 지속적 통합)는 별도 상태입니다.

## 13. 남은 범위

- Cache Stampede(캐시 만료 순간 동시 DB 조회 집중) 방지용 분산 잠금 또는 요청 합치기
- 외부 Metrics Exporter와 Dashboard
- 다중 API/Silo 운영 환경의 부하 측정과 timeout 재조정
- Redis 인증·TLS(Transport Layer Security, 전송 계층 보안)·관리형 서비스 설정
- 첫 페이지 이외의 캐시가 실제로 필요한지에 대한 측정

현재 2분 TTL과 100ms timeout은 시작값입니다. 운영 지표 없이 최적값이라고 단정할 수 없으며, 부하 시험 결과로 조정해야 합니다.
