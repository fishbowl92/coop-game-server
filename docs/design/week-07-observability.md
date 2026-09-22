# 7주차: OpenTelemetry 운영 관측성

기준일: 2026-09-23. 이 문서는 API·Orleans·PostgreSQL·Redis 흐름을 로그·지표·분산 추적으로 연결하는 구현 계약이다.

## 1. 목표

다음 질문에 코드 수정이나 디버거 연결 없이 답할 수 있어야 한다.

1. 한 HTTP 요청이 API와 어느 Grain을 거쳐 PostgreSQL·Redis에서 시간을 사용했는가?
2. 관리자 보상은 새로 적용됐는가, 기존 결과가 재생됐는가, 업무 오류로 거절됐는가?
3. 진행도 캐시는 적중·누락·손상·오류 중 어느 결과였고 PostgreSQL 대체 조회가 얼마나 걸렸는가?
4. Redis 또는 PostgreSQL 장애가 발생했을 때 어느 구간에서 실패했고 원본 작업은 어떤 결과로 끝났는가?
5. 수집 데이터에 비밀번호·JWT·연결 문자열·요청 본문이 들어가지 않았는가?

관측성은 업무 성공 조건을 바꾸지 않는다. OTLP(OpenTelemetry Protocol, 관측 데이터 전송 표준) 수신기가 꺼져 있어도 보상·조회·캐시 대체 동작은 기존 계약을 유지해야 한다.

## 2. 책임 경계

| 구성 요소 | 책임 |
|---|---|
| `CoopGameServer.Observability` | 공통 ActivitySource·Meter 이름, OTLP 내보내기, Runtime·ASP.NET Core·Orleans·Npgsql 수집 정책 |
| API | HTTP Server Activity 시작, 인증·인가 뒤 Orleans 호출, `CoopGameServer.Api` 서비스 이름 |
| Orleans Client·Silo | W3C Trace Context를 메시지에 전달·복원, Grain 실행 Span과 `Microsoft.Orleans` 지표 |
| `PostgreSqlRewardWriter` | 보상 저장 하위 Activity, 적용·재생·거절·예외 결과와 저장 지연 지표 |
| `RedisPlayerProgressionCache` | 읽기·쓰기·무효화 Activity와 기존 캐시 결과·오류·지연 지표 |
| Npgsql | 이름이 `GameDb`인 연결 풀의 SQL Activity와 연결·명령 지표 |
| Aspire Dashboard | 개발 PC에서 OTLP 로그·지표·추적을 조회하는 화면 |

PostgreSQL은 보상·멱등성의 최종 원본이고 Redis는 삭제 가능한 읽기 캐시라는 기존 책임을 그대로 유지한다.

## 3. 분산 추적 경로

관리자 보상:

```text
HTTP POST /api/players/{playerId}/rewards
  -> Orleans Client Activity
  -> PlayerGrain.GrantAdminRewardAsync
  -> postgresql.reward.write
     -> Npgsql command Activities
  -> redis.progression.invalidate
```

진행도 첫 페이지:

```text
HTTP GET /api/players/{playerId}/progression
  -> Orleans Client Activity
  -> PlayerGrain.GetProgressionPageAsync
  -> redis.progression.read
  -> cache miss/error/corrupt이면 Npgsql command Activities
  -> redis.progression.write
```

API Client와 Silo는 Orleans `AddActivityPropagation`을 사용한다. 한 요청의 모든 Activity는 같은 Trace ID를 사용하고 각 작업은 Parent Span ID로 연결한다.

## 4. 지표 계약

| Meter | Instrument | 태그 |
|---|---|---|
| `CoopGameServer.Rewards` | `coopgame.reward.persistence.operations` | `operation`, `result` |
| `CoopGameServer.Rewards` | `coopgame.reward.persistence.duration` | `operation`, `result` |
| `CoopGameServer.PlayerProgressionCache` | 기존 request·fallback·Redis error·duration·DB fill | 기존 `result`, `reason`, `operation` |
| `Microsoft.Orleans` | Orleans Runtime 제공 지표 | Orleans 제공 낮은 종류 수의 태그 |
| `Npgsql` | 연결 풀·명령 지표 | 고정 풀 이름 `GameDb` 등 Npgsql 표준 태그 |
| .NET·ASP.NET Core | GC·Runtime·HTTP·Kestrel·Routing·Rate limit | 프레임워크 표준 태그 |

`requestId`, `playerId`, 관리자 Account ID, 닉네임, 보상 사유는 지표 태그에 넣지 않는다. 값 종류가 계속 늘어나는 태그는 시계열 수와 저장 비용을 폭증시키기 때문이다. 요청·Player 식별자는 개별 Trace에서만 사용한다.

## 5. 비밀 정보 규칙

- Authorization 헤더, JWT, 비밀번호, 요청·응답 본문을 수집하지 않는다.
- ASP.NET Core의 자동 예외 상세 기록을 끈다. 예외 메시지는 DB 연결 문자열이나 입력값을 포함할 수 있으므로 직접 만든 Activity에는 예외 형식만 기록한다.
- Npgsql 연결 풀에는 `GameDb`라는 고정 이름을 지정한다. 기본 풀 이름으로 연결 문자열이 노출되는 것을 막는다.
- Redis 키와 캐시 JSON을 Activity·지표에 기록하지 않는다.
- 로컬 Aspire Dashboard 포트는 `127.0.0.1`에만 바인딩한다. 익명 접근 설정은 개발 PC 전용이며 운영 배포 설정으로 사용하지 않는다.

## 6. 수집과 조회

API와 Silo는 `Development` 환경에서 기본적으로 `http://localhost:4317`로 OTLP/gRPC를 전송한다. 환경 변수 `OTEL_EXPORTER_OTLP_ENDPOINT`가 있으면 이를 우선한다. 주소가 없으면 수집기는 등록하되 Exporter(내보내기)는 만들지 않는다.

Compose의 `observability-dashboard`는 Microsoft Aspire Dashboard `13.5.2` 이미지를 사용한다.

- 화면: `http://localhost:18888`
- OTLP/gRPC: `127.0.0.1:4317`
- OTLP/HTTP: `127.0.0.1:4318`

## 7. 실패 계약

- OTLP 수신기 중단은 백그라운드 내보내기 실패일 뿐 게임 요청을 실패시키지 않는다.
- Redis 읽기 실패는 기존대로 PostgreSQL 대체 조회로 전환하고 `result=error`, `reason=error`를 남긴다.
- Redis 쓰기·무효화 실패는 DB 성공을 되돌리지 않고 실패 Activity와 Redis 오류 지표를 남긴다.
- PostgreSQL 예외는 업무 오류로 위장하지 않고 호출자에게 전달하며 `postgresql.reward.write` Activity를 Error로 표시한다.
- 멱등성 충돌·없는 Player·권한 회수는 예외가 아닌 정의된 결과 태그로 구분한다.

## 8. 완료 기준

- API와 Silo의 로그·지표·추적이 OTLP로 내보내진다.
- HTTP 관리자 보상 Trace와 PostgreSQL 보상 Activity가 같은 Trace ID를 사용한다.
- 보상 적용·재생·거절·예외 지표가 낮은 Cardinality 태그만 사용한다.
- Redis 적중·대체 조회·오류·무효화 구간을 Trace와 지표로 확인할 수 있다.
- Npgsql 풀 이름과 관측 태그에 연결 문자열·비밀번호가 없다.
- Dashboard 없이도 기존 업무 테스트가 통과한다.
- Compose 설정 검사, Release 빌드, 전체 단위·통합 테스트가 통과한다.

## 9. 범위 밖

- 운영용 장기 저장소, 경보 전송, 온콜 절차와 SLO(Service Level Objective, 서비스 수준 목표)
- 운영 Dashboard 인증·TLS(Transport Layer Security, 전송 계층 보안)·외부 공개
- 브라우저 새로고침 뒤 관리자 미확정 요청 복원
- 학습센터와 Notion 페이지 동기화

공식 기준은 [.NET OpenTelemetry 관측성](https://learn.microsoft.com/dotnet/core/diagnostics/observability-with-otel), [Orleans 관측성](https://learn.microsoft.com/dotnet/orleans/host/monitoring/), [Npgsql 진단](https://www.npgsql.org/doc/diagnostics/overview.html), [Standalone Aspire Dashboard](https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone)다.

## 10. 구현 결과

| 파일 | 추가·변경한 기호 | 매개변수·상태 | 이유 |
|---|---|---|---|
| `CoopGameServerObservabilityExtensions.cs` | `AddCoopGameServerObservability`, `AddCoopGameServerOpenTelemetryLogging` 추가 | `defaultServiceName`, `includeAspNetCore`, OTLP Endpoint | API와 Silo에 같은 Resource·Exporter·수집 정책을 적용한다. ASP.NET Core 자동 예외 상세 기록은 비밀 노출을 막기 위해 끈다. |
| `CoopGameServerTelemetry.cs` | `StartActivity`, `MarkError`, `RecordRewardPersistence` 추가 | 고정 Activity 이름, `operation`, `result`, 지연시간 | 업무 구간과 보상 결과를 기록하되 예외 메시지와 고카디널리티 식별자를 지표에서 제외한다. |
| API·Silo `Program.cs` | OpenTelemetry 등록, `AddActivityPropagation`, 이름 있는 Npgsql DataSource 연결, Content Root 고정 | 서비스 이름 `CoopGameServer.Api`·`CoopGameServer.Silo` | HTTP와 Grain 추적을 연결하고 어느 폴더에서 실행해도 각 프로젝트 설정 파일을 읽게 한다. |
| `GameDbDataSourceFactory.cs` | `Create`, 내부 `CreateBuilder` 추가 | 비밀 연결 문자열 입력, 고정 이름 `GameDb` | Npgsql 지표의 풀 이름에 연결 문자열이 사용되는 것을 막는다. |
| `PostgreSqlRewardWriter.cs` | `WriteAsync` 관측 래퍼, `WriteCoreAsync`, `GetTelemetryResult` 추가 | 적용·재생·업무 오류·예외 결과 | 기존 Transaction·멱등성 코드를 바꾸지 않고 저장 결과와 지연을 추적한다. |
| `RedisPlayerProgressionCache.cs` | 읽기·쓰기·무효화 Activity와 `StartCacheActivity` 추가 | 고정 operation·result | Redis 키와 Player ID 없이 적중·누락·손상·오류 구간을 구분한다. |
| `compose.yaml`, 시작 스크립트 | Aspire Dashboard와 준비 상태 판정 추가 | loopback UI·OTLP 포트 | 로컬 수집기를 한 명령으로 시작하고 Healthcheck가 없는 Dashboard는 `running`으로 판정한다. |

삭제한 함수나 공개 계약은 없다. `PostgreSqlRewardWriter.WriteAsync`의 기존 본문은 비공개 `WriteCoreAsync`로 옮겼으며 Transaction, 행 잠금, 멱등성 재생·충돌 판정 순서는 유지했다.

## 11. 검증 기록

검증일은 2026-09-23이며 소스는 구현 커밋 `1779dbb`와 테스트 커밋 `7a4197c`를 포함한다.

- `dotnet build CoopGameServer.slnx --configuration Release`: 경고 0개, 오류 0개.
- `dotnet test CoopGameServer.slnx --configuration Release --no-build`: 단위 141개와 통합 139개, 총 280개 통과.
- 관측성 집중 테스트: 지표·예외·Npgsql 이름 3개, HTTP Trace 전파·실제 Redis 실패 2개 통과.
- `docker compose config -q`: 비밀값 출력 없이 Compose 구성 검사 통과.
- `tools/Start-LocalEnvironment.ps1 -HealthTimeoutSeconds 60`: PostgreSQL·Redis `healthy`, Dashboard `running` 판정 통과.
- Dashboard `http://localhost:18888`: HTTP 200. 실제 Development API·Silo를 실행하고 일반 사용자 진행도 조회 후 Traces 화면에서 `CoopGameServer.Api`, `CoopGameServer.Silo`, PostgreSQL 구간 수신을 확인했다.
- 변경한 C# 파일만 지정한 `dotnet format --verify-no-changes --include ...`: 통과. 전체 솔루션 검사는 이번 범위 밖 기존 파일의 줄바꿈·인코딩 위반 때문에 실패하며 이번 변경 파일에는 새 형식 위반이 없다.

테스트가 증명하는 경계는 다음과 같다. HTTP 통합 테스트는 실제 JWT 인증, API 시작 구성, Orleans Client·Silo 전파와 PostgreSQL Writer의 동일 Trace ID를 검증한다. Redis 통합 테스트는 실제 Redis 지연·중단에서도 오류가 업무 경계 밖으로 나오지 않고 Activity에 Player ID와 키 접두사가 기록되지 않음을 검증한다. 로컬 smoke test는 실제 OTLP 수신과 Dashboard 표시를 검증한다.
