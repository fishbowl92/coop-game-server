# 4주차 구현 정리 — 전투·연결·재접속

기준: 2026-09-14 로컬 작업 트리

이 문서는 설계의 미래 계획과 현재 구현을 구분하기 위한 코드 검토 기록이다. 커밋·푸시·GitHub CI 성공을 뜻하지 않는다. 2026-09-14 백업 후 개발 DB에 새 마이그레이션을 적용하고 이력·열·인덱스·CHECK 제약을 확인했다.

## 1. 구현 흐름

1. 매칭으로 정확히 네 명의 Ready 방을 만든다
2. 참가자는 JWT(JSON Web Token, 서명된 로그인 토큰)로 인증한 뒤 Connect를 호출한다
3. 서버가 connectionId와 증가하는 generation(연결 세대)을 발급하고 PostgreSQL에 저장한다
4. 네 참가자의 Lease(연결 유지 기한)가 모두 유효할 때 StartCombat을 허용한다
5. 공격·스킬은 참가자, 현재 연결, 명령 순번, 서버 시각의 쿨다운을 검사한다
6. 피해량·적 반격·웨이브 이동·최종 승패는 서버가 계산한다
7. 방 상태·참가자 상태·최초 요청 결과·완료 시 결과 전달 행을 함께 저장한다
8. 커밋 성공 뒤 메모리 후보를 채택하고 파티 복귀·티켓 완료·개별 보상 전달을 조정한다
9. 실패한 후처리는 영속 표식과 복구 작업자가 다시 처리한다

체력과 연결 상태는 별개다. Disconnected라도 살아 있는 참가자는 적 공격 대상이다. 유예가 끝나 Abandoned가 되면 체력을 0으로 만들며, 전원이 전투 불능이면 Defeat로 확정한다.

## 2. 연결 시간과 재접속

현재 학습용 기본 정책은 Lease 15초, 복귀 유예 30초, 최초 입장 제한 30초다. 클라이언트는 보통 5초마다 Heartbeat(생존 신호)를 보낸다. 연결 규칙 클래스는 Lease·Grace를 생성자로 받지만 실제 방은 현재 Default 정책을 사용한다. 운영 설정 파일에서 이 값을 변경하는 연결은 운영 확장 사항이며 구현된 것으로 주장하지 않는다.

- 기한과 정확히 같은 시각은 이미 만료다
- 자동 만료는 평가를 실행한 현재 시각이 아니라 기존 Lease 기한에서 유예를 계산한다
- Disconnect는 정상 종료 요청을 받은 서버 시각에서 유예를 계산한다
- 끊어진 연결 ID는 제거하지만 세대는 보존한다
- Reconnect는 예상 세대가 현재 세대와 같아야 한다
- 성공 시 새 ID를 발급하고 세대를 하나 증가시킨다
- 이전 발급 요청을 재생하더라도 이미 교체·만료된 자격은 반환하지 않는다
- 재접속은 체력·쿨다운·승인 순번을 초기화하지 않는다

최초 입장 기한이 지난 Ready 방은 StartedAt을 채우지 않고 Cancelled로 종료한다. 진행 중인 방은 최초 입장 기한 자체로 취소하지 않는다. 기존 버전에서 넘어온 InGame 방의 미접속 참가자만 별도 최초 유예를 적용한다.

## 3. 새 파일과 함수의 역할

| 파일 | 함수·데이터 | 생성·변경 이유 |
|---|---|---|
| `src/CoopGameServer.Domain/GameRooms/GameRoomConnectionRules.cs` | Connect, Reconnect, Heartbeat, Disconnect, Evaluate, Close, CanReplayCredential | 시간과 연결 교체를 DB·네트워크 없이 계산 |
| `src/CoopGameServer.GrainContracts/GameRooms/GameRoomConnectionCommand.cs` | 명령 종류, 요청 ID, 인증 PlayerId, 연결 ID·세대, 전용 응답 | HTTP 입력과 내부 인증된 요청의 경계 및 재생 계약 |
| `src/CoopGameServer.GrainContracts/GameRooms/GameRoomCombatCommand.cs` | ConnectionId, ConnectionGeneration | 이전 클라이언트의 새 공격 차단 |
| `src/CoopGameServer.Grains/GameRooms/GameRoomState.Connections.cs` | ExecuteConnection(command, now, newId) | 후보 연결 변경·최초 응답 보존·네 명의 시작 조건 검사 |
| `src/CoopGameServer.Grains/GameRooms/GameRoomState.Deadlines.cs` | EvaluateDeadlines(now), CloseConnections() | 반복 가능한 만료 평가, 취소·패배, 종료 자격 폐기 |
| `src/CoopGameServer.Grains/GameRooms/GameRoomState.cs` | _connections, GetConnection, Clone, Restore, Get | 공개 스냅샷과 비공개 자격을 분리하고 후보 복제 |
| `src/CoopGameServer.Grains/GameRooms/GameRoomGrain.cs` | ExecuteConnectionAsync, GetPlayerViewAsync, ReconcileDeadlinesAsync, PersistReconciliationAsync | 실제 저장·복원·인증된 참가자용 조회 연결 |
| 같은 파일 | _deadlineTimer, EnsureDeadlineTimer, OnDeactivateAsync | 활성 방을 1초 주기로 평가하고 종료 시 타이머 해제 |
| 같은 파일 | ExecuteCombatAsync, CreateRequestRecord, RestoreStoredRequest | 현재 연결 검사와 전용 요청·응답 JSON 저장·복원 |
| 같은 파일 | FinalizeCompletedRoomAsync | 시작 전 취소에서도 당시 티켓 리더와 고정된 요청 ID로 파티 복귀 |
| `src/CoopGameServer.Grains/GameRooms/GameRoomRecoveryProcessor.cs` | FindDueRoomIdsAsync, RecoverDueRoomsAsync | 결과 전달뿐 아니라 최초 입장·Lease·유예 만료 방 재활성화 |
| `src/CoopGameServer.Persistence/GameRooms/GameRoomPlayerRecord.cs` | 연결 상태·ID·세대·시각 8개 필드, ReadConnection, UpdateConnection | 프로세스 재시작 후에도 최신 자격과 기한 보존 |
| `src/CoopGameServer.Persistence/GameRooms/GameRoomConnectionSchema.cs` | CheckConstraint | NULL 비교까지 고려해 모순된 연결 행 거부 |
| `src/CoopGameServer.Persistence/GameRooms/GameRoomRecord.cs` | InitialConnectDeadline, CancellationReason, UpdateConnectionLifecycle | 최초 기한과 시작 전 취소 이유 저장 |
| `src/CoopGameServer.Api/Controllers/GameRoomPlayController.cs` | 연결·전투·자기 상태 HTTP 엔드포인트 | JWT의 본인 ID만 사용하고 피해량·보상을 입력받지 않음 |
| `src/CoopGameServer.Api/Authentication/GameRoomRateLimits.cs` | AddGameRoomRateLimits | 본인+방 단위 Heartbeat 초당 2회, Reconnect 분당 5회 제한 |
| `src/CoopGameServer.Api/Controllers/GameRoomsController.cs` | Complete 입력 제한 | HTTP로 임의 승리·패배를 확정하는 경로 차단 |

기존 API의 관리자 Start는 진단용으로 남아 있다. 일반 참가자는 `start-combat`을 사용한다. 실제 승패는 전투 처리로 결정하며 관리자 Complete에는 Cancelled만 허용한다. 클래스나 함수를 삭제하지 않고 기존 계약의 사용 범위를 제한했다.

## 4. HTTP 사용 순서

모든 경로의 앞부분은 `/api/game-rooms/{roomId}`이며 Authorization 헤더에 Bearer JWT가 필요하다.

| 경로 | 본문 | 의미 |
|---|---|---|
| POST `/connect` | requestId | 최초 연결 자격 발급 |
| POST `/reconnect` | requestId, expectedGeneration | 현재 세대와 비교 후 연결 교체 |
| POST `/heartbeat` | connectionId, generation | 현재 연결 Lease 연장 |
| POST `/disconnect` | requestId, connectionId, generation | 명시적으로 유예 시작 |
| POST `/start-combat` | requestId, connectionId, generation | 네 명의 유효한 연결 확인 후 시작 |
| POST `/basic-attack` | requestId, connectionId, generation, sequence, knownStateVersion(선택) | 일반 공격 |
| POST `/use-skill` | 위와 동일 | 스킬 |
| GET `/play-state` | 없음 | 현재 전투 상태와 본인의 세대 조회, 연결 ID는 반환하지 않음 |

본문에 PlayerId를 받지 않는다. 방의 다른 참가자에게는 연결 상태 이름만 공개하고 연결 ID·세대·Lease는 공개하지 않는다. 오류 응답에도 연결 자격을 포함하지 않는다.

## 5. 저장 실패와 재시도

후보 상태는 현재 메모리를 복제한 사본이다. SaveChanges 또는 Commit이 실패하면 기존 메모리는 유지된다. DB 성공 후 응답이 유실되면 같은 requestId로 최초 응답을 재생한다. 같은 키로 다른 명령을 보내면 충돌한다.

Heartbeat는 이력을 추가하지 않고 현재 행만 저장한다. 반면 Connect·Reconnect·Disconnect·StartCombat은 최초 명령과 응답을 `game_room_requests`에 보존한다. 현재 구현은 이 종류들을 `command_kind=Connection`으로 묶고 JSON의 Action으로 세부 동작을 구분한다. 전투 명령과 같은 방별 요청 키 공간을 사용한다.

반복 평가가 같은 상태를 발견하면 버전을 다시 증가시키지 않는다. 만료로 완료되는 경우 방·참가자·결과 대기 행·FinalizationPending을 같은 트랜잭션에서 저장한다. 파티·티켓 등 외부 Grain 호출 실패는 완료된 결과를 되돌리지 않는다.

## 6. 검증 근거와 범위

최종 전체 실행 결과: `dotnet test CoopGameServer.slnx --configuration Release --no-restore` — 단위 108개·통합 122개, 총 230개 통과. 새 저장 테스트만 따로 실행한 결과가 아니라 기존 전체 회귀 검사까지 포함한 결과다.

- 순수 테스트: Lease·Grace의 기한 경계, 이전 자격 차단, 후보 복사 격리, 최초 요청 재생, 최초 기한 취소, 전원 포기 패배
- PostgreSQL·Orleans 테스트: 재시작 후 연결 및 전투 복원, 동시 재접속, 요청 이력 비누적, 저장 실패 시 세대·체력 롤백
- 실제 HTTP 테스트: Program의 JWT 인증·인가·라우팅·호출 제한을 통과해 실제 테스트 Grain 호출, 401·403·409·429 확인
- 전체 회귀 테스트: 기존 파티·매칭·보상·마이그레이션 보존 검사를 함께 실행

HTTP 테스트는 localhost 운영 Silo 대신 TestCluster를 주입한다. 운영 배포·TLS 인증서·다중 API 분산 호출 제한·실제 Unity 클라이언트 연결을 검증한 것은 아니다. 현재 방의 정책 값은 고정된 학습용 기본값이다.

## 7. 마이그레이션과 인수 절차

이번 작업 트리에는 전투 요청 저장, 완료 후처리 추적, 연결 상태, 연결 요청 기록, 최초 연결 기한, 연결 생명주기 제약 마이그레이션이 포함된다. 자동 검증은 일회용 테스트 DB에 적용하며, 개발 DB에도 별도 백업 뒤 동일 이력 적용을 완료했다.

개발 DB 적용 전 기존 데이터 백업과 변경 검토를 수행했다. 연결 기한 마이그레이션은 과거 활성 방에 적용 시점부터 30초의 최초 유예를 한 번 부여하고, 과거 완료 방의 연결 상태는 Left로 기록한다. 기존 승패를 새로 추측하지 않는다. 새 시작 전 취소 행이 있는 DB에서 이전 제약으로 단순 롤백하면 실패할 수 있으므로 Down은 데이터 복구 계획의 대체 수단이 아니다.

핵심 코드와 개발 DB 적용 뒤 남는 인수 작업은 목적별 커밋, push, 해당 커밋의 CI 확인이다. Notion 문서는 이번 코드 작업에서 변경하지 않았다.
