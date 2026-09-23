# 8주차 HTTP 부하 기준 측정

측정일: 2026-09-23 (Asia/Seoul). 대상 구현 커밋: `fe61f83` (로컬). [실험 계약](../../design/week-08-load-testing.md), [원시 요청 표본](raw-samples.csv), [실행별 JSON](runs.json).

## 목적과 판정

실제 JWT(JSON Web Token, 서명된 인증 토큰) 인증을 거치는 진행도 조회와 실제 매칭·접속을 거치는 유효 전투 명령이 지정한 도착률에서 처리되는지 확인했다. 낮은 로컬 기준은 업무 성공률 100%, 예상 밖 HTTP 오류 0건, 자체 계산 p95(95번째 백분위 지연) 2초 이하이다. 모든 실험은 이를 만족했다. 측정에서 재현되는 성능 병목을 확인하지 못했으므로 게임 서버 코드는 변경하지 않았다.

## 재현 조건

- Windows 10.0.26100, AMD Ryzen 7 5800H 8코어/16논리 프로세서, 물리 메모리 17,024,741,376바이트(약 15.86 GiB). .NET SDK 10.0.302, Docker Engine 29.6.1, NBomber 6.6.0.
- 단일 API, 단일 Orleans Silo, PostgreSQL 17 alpine, Redis 7 alpine. 별도 Compose 프로젝트와 루프백 포트 25432·26379을 사용했다. PostgreSQL 데이터는 컨테이너 tmpfs(메모리 기반 파일 시스템)에 두어 반복마다 초기화했다. 이 구성의 디스크 지연은 운영 디스크를 대표하지 않는다.
- Staging 환경, JWT 서명 키·DB 비밀번호는 실행마다 임시 생성. 보고서에는 기록하지 않았다.
- 진행도: 8개 시험 계정을 등록해 첫 페이지를 한 번씩 조회한 뒤, 20건/초로 10초 측정했다. 캐시 적중 자체를 독립적으로 계측한 결과는 없다.
- 전투: 네 명씩 매칭해 방을 만들고 모두 접속한 뒤 전투를 시작했다. 방마다 네 플레이어가 각 한 번씩 스킬을 사용한다. 연결 ID·세대와 각 플레이어의 첫 명령 순번 1을 보낸다. 2건/초와 5건/초를 각각 8초 측정했다. 전투 공격 예열 없이 첫 공격의 지연도 표본에 포함했다.
- 실행별로 새 DB와 새 계정을 사용했다. 준비·마이그레이션·서버 시작·사후 검사는 요청 지연 표본에서 제외했다. 각 조건을 같은 커밋·설정에서 3회 실행했다.
- 자체 p50/p95/p99는 성공 표본을 정렬한 뒤 `ceil(p × n)`번째 값을 쓰는 nearest-rank 방식이다. 표본이 16개인 2건/초 전투에서는 p95가 최대값과 같아 NBomber 화면의 보간 방식 p95와 다르다.

## 결과

| 경로 | 도착률 | 표본/성공 (3회) | 실행별 자체 p95 (ms) | 실행별 자체 p99 (ms) |
| --- | ---: | --- | --- | --- |
| 진행도 첫 페이지 | 20건/초 | 200/200, 200/200, 200/200 | 8.81, 4.72, 4.80 | 31.81, 9.27, 10.79 |
| 유효 스킬 명령 | 2건/초 | 16/16, 16/16, 16/16 | 195.80, 196.34, 197.35 | 195.80, 196.34, 197.35 |
| 유효 스킬 명령 | 5건/초 | 40/40, 40/40, 40/40 | 77.68, 29.81, 35.34 | 204.14, 194.18, 193.77 |

총 768/768건이 유효 응답이었다. 업무 거부, 예상 밖 HTTP 응답, 네트워크 예외는 없었다. 전투 첫 요청은 각 반복에서 약 194~204 ms로 나머지 요청보다 길었다. 5건/초에서는 두 번째 요청도 약 175~184 ms였다. 첫 요청 순서와 지연의 연관은 원시 표본에서 확인되지만, 원인이 JIT(Just-In-Time, 실행 시 컴파일)·DB(Database, 데이터베이스) 연결·Grain 활성화 중 무엇인지는 이 실험만으로 식별할 수 없다. 지속적인 포화나 오류의 증거는 없다.

## 사후 정합성 범위

모든 반복에서 시험 계정 수, 음수 재화·인벤토리 부재를 DB에서 확인했다. 진행도 응답의 골드·인벤토리를 PostgreSQL 원본과 대조했다. 전투는 방마다 참가자 4명, 성공 응답 수와 고유 전투 요청 기록 수 일치, 플레이어별 명령 순번 연속, 미완료 방의 결과·보상 기록 부재를 확인했다. 검사는 모두 읽기 전용이다.

각 방은 첫 웨이브의 일부만 진행하므로 이 부하 실행 자체는 완료 시 보상 정확히 한 번 지급을 검증하지 않는다. 해당 기능의 별도 통합 테스트와도 검증 범위를 구분한다. 이 PC의 단일 Silo·tmpfs DB 결과를 운영 서비스 수준 목표나 최대 처리량으로 해석할 수 없다.

## 구현 요소와 변경 이유

- [부하 실행기](../../../tests/CoopGameServer.LoadTests/Program.cs): `LoadOptions.FromEnvironment()`가 시나리오·주소·DB 연결·주입률·측정 시간·시간 제한·보고서 경로를 읽어 잘못된 입력을 시작 전에 거부한다. `RegisterAsync(index)`는 고유 계정을 만들고 JWT를 받는다. `GetAsync<T>(path, client)`, `PostAsync<T>(path, body, client)`, `SendAsync(method, path, body, client)`는 준비 요청과 측정 요청에 동일한 실제 HTTP 인증 경로를 사용한다. `ClassifyFailure(status)`는 업무 충돌·본문 오류·다른 HTTP 오류를 구분한다.
- 같은 파일의 `LoadPlayer`는 `PlayerId`, JWT, 서버가 발급한 `ConnectionId`와 `Generation`을 보유한다. `LoadRoom(RoomId, Players)`은 네 참가자의 방 소속을, `LoadSample(Ordinal, StatusCode, Classification, ElapsedMs)`은 비밀값 없는 원시 표본을 기록한다. 측정 후 EF Core(Entity Framework Core, C# 객체와 데이터베이스를 연결하는 도구) 읽기 전용 질의로 영속 불변식을 검사한다.
- [실행 스크립트](../../../tools/Run-Week08Load.ps1): `Scenario`, `Rate`, `DurationSeconds`, `Repeats`, `ReportRoot` 매개변수로 조건을 고정한다. `Stop-TestProcesses`는 이 스크립트가 시작한 API·Silo 프로세스만 종료하고, 전용 Compose 프로젝트만 내린다. 비밀번호·JWT 키는 임시 환경 변수로 생성하고 복구한다.
- [전용 Compose](../../../tests/CoopGameServer.LoadTests/compose.yaml)는 개발 DB와 다른 포트·tmpfs를 사용한다. [솔루션](../../../CoopGameServer.slnx)에 별도 실행 프로젝트를 등록하고 [.gitignore](../../../.gitignore)에 실행 로그 경로를 추가했다. 기존 게임 API·Grain·영속성 함수나 매개변수는 변경·삭제하지 않았다.

## 실행 및 다음 판단

저장소 루트에서 아래 명령은 각각 전용 DB·Redis를 시작하고, 마이그레이션과 Release 빌드 후 3회 실행하며 종료 시 전용 프로세스와 컨테이너를 정리한다. 원본 NBomber 보고서와 프로세스 로그는 Git에서 제외한 `artifacts/week08/`에 남는다.

```powershell
./tools/Run-Week08Load.ps1 -Scenario progression -Rate 20 -DurationSeconds 10 -Repeats 3
./tools/Run-Week08Load.ps1 -Scenario combat -Rate 2 -DurationSeconds 8 -Repeats 3
./tools/Run-Week08Load.ps1 -Scenario combat -Rate 5 -DurationSeconds 8 -Repeats 3 -ReportRoot artifacts/week08/combat-rate5
```

위 첫 명령은 따뜻한 진행도 첫 페이지를, 둘째·셋째 명령은 유효 전투 스킬을 측정한다. 더 높은 부하나 첫 명령 지연을 개선하려면, 먼저 명령별 Trace와 PostgreSQL·Orleans 지표를 같은 조건에서 수집해 시간을 소비하는 구간을 확인해야 한다. 이 보고서에는 근거 없는 After 최적화 수치를 넣지 않았다.

검증: Release 솔루션 빌드 경고 0·오류 0. 전체 테스트 141개 단위 + 139개 통합 = 280개 통과. 이 로컬 검증은 `fe61f83` 구현 상태를 대상으로 했다. 동일 구현과 보고서를 포함한 `76939f7`에서 [GitHub Actions CI 실행 35850550530](https://github.com/fishbowl92/coop-game-server/actions/runs/35850550530)의 복원·빌드·테스트가 성공했다.
