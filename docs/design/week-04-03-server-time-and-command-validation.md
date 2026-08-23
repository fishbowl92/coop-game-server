# 4주차 세부 설계 03 — 서버 시간·전투 명령·쿨다운 검증

- 문서 상태: 제안됨(Proposed)
- 최초 작성일: 2026-08-21
- 구현 상태: 1차 심층 검토 반영, 코드 미구현
- 상위 문서: [4주차 전체 설계 — 게임 룸·전투·재접속](week-04-game-room-reconnect-overview.md)
- 선행 문서: [GameRoom 전투·웨이브 상태 머신](week-04-02-game-room-combat-state-machine.md)

## 1. 문서 목적

클라이언트가 “공격했다”, “스킬 쿨다운이 끝났다”, “적에게 9999 피해를 줬다”고 보내더라도 서버가 그대로 믿어서는 안 된다.

이 문서는 다음을 정의한다.

- 외부 전투 요청과 내부 Grain 명령의 경계
- `requestId`와 `commandSequence`의 서로 다른 역할
- 서버 시간을 이용한 쿨다운 검증
- 일반 공격과 스킬의 최소 규칙
- 중복·역순·누락 명령 처리
- 오류 코드와 자동 테스트 기준

## 2. 위협과 오류 모델

전투 명령은 악의가 없어도 다음 이유로 중복되거나 순서가 바뀔 수 있다.

- 클라이언트가 응답을 받지 못해 같은 요청을 재전송
- 모바일 네트워크 전환으로 요청 지연
- 사용자가 버튼을 빠르게 여러 번 누름
- 서로 다른 HTTP 연결의 도착 순서 변경
- 오래된 화면에서 이전 방 명령 전송
- 조작된 클라이언트가 쿨다운·피해량·Player ID를 위조

따라서 요청을 받은 사실과 명령을 적용해도 되는지는 별도로 판단한다.

## 3. 신뢰 경계

### 3.1 클라이언트가 보낼 수 있는 값

- `requestId`: 네트워크 재시도 식별자
- `commandSequence`: Player가 이 방에서 보낸 명령 순번
- `connectionId`: 서버가 현재 접속에 발급한 연결 식별자
- `connectionGeneration`: 재접속 때마다 증가하며 이전 연결을 구분하는 세대 번호
- 명령 종류: 일반 공격 또는 스킬
- 현재 클라이언트가 알고 있는 `stateVersion`: 선택적 충돌 진단 정보

### 3.2 서버가 결정하는 값

- Player ID: JWT Claim에서 읽음
- Room 참가 여부
- 명령 처리 가능 상태
- 실제 피해량
- 쿨다운 시작·완료 시각
- 적 반격 대상과 피해량
- 웨이브 완료
- 게임 승리·실패
- 최종 보상

클라이언트 요청에는 피해량, 적 체력, 보상 수량을 포함하지 않는다.

## 4. HTTP 요청과 Grain 명령

외부 요청 DTO(Data Transfer Object, 계층 간 전달 데이터)는 최소 정보만 가진다.

```csharp
public sealed record ExecuteCombatActionRequest(
    Guid? RequestId,
    Guid ConnectionId,
    long ConnectionGeneration,
    long CommandSequence,
    long? KnownStateVersion);
```

API는 JWT의 Player ID와 경로의 Room ID를 합쳐 Grain 내부 명령을 만든다.

```csharp
public sealed record ExecuteGameRoomActionCommand(
    Guid RequestId,
    Guid PlayerId,
    Guid ConnectionId,
    long ConnectionGeneration,
    long CommandSequence,
    GameRoomActionKind ActionKind,
    long? KnownStateVersion);
```

```csharp
public enum GameRoomActionKind
{
    BasicAttack = 0,
    UseSkill = 1,
}
```

일반 공격과 스킬을 별도 HTTP 경로로 만들더라도 Grain 내부에서는 같은 명령 형식과 검증 순서를 재사용한다.

모든 새 전투 명령은 현재 `game_room_players` 행의 `connection_id`와 `connection_generation`이 둘 다 일치해야 한다.
JWT가 Player 신원을 증명하더라도 이전 연결의 명령까지 최신으로 만들지는 않으므로 이 검사를 생략하지 않는다.

## 5. requestId와 commandSequence

두 값은 목적이 다르다.

| 값 | 질문 | 막는 문제 |
|---|---|---|
| `requestId` | 이 네트워크 요청을 전에 처리했는가? | 같은 요청 재전송의 중복 적용 |
| `commandSequence` | 이 Player의 다음 명령 순번이 맞는가? | 지연·역순·누락 명령 적용 |
| `connectionId + generation` | 이 명령이 현재 활성 연결에서 왔는가? | 재접속 뒤 이전 연결이 보낸 명령 적용 |
| `stateVersion` | 클라이언트가 어느 방 상태를 보고 명령했는가? | 오래된 화면 진단과 최신 스냅샷 안내 |

### 5.1 requestId 범위

전투 명령의 멱등성 범위는 `(roomId, requestId)`다.

- 같은 Room + 같은 requestId + 같은 명령 본문: 최초 결과 재생
- 같은 Room + 같은 requestId + 다른 본문: `RequestIdConflict`
- 다른 Room에서 같은 requestId: 독립 요청

### 5.2 commandSequence 규칙

- Player의 첫 전투 명령 순번은 1이다.
- 성공적으로 적용된 명령마다 1씩 증가한다.
- 다음 허용 값은 `LastAcceptedCommandSequence + 1`이다.
- 값이 더 작으면 이미 지난 명령이다.
- 값이 더 크면 중간 명령이 누락된 상태다.
- 거부된 쿨다운 명령은 순번을 소비하지 않는다.
- 같은 requestId 재생은 순번을 다시 증가시키지 않는다.

DB에는 요청이 주장한 순번과 성공적으로 승인한 순번을 분리한다.

- 요청이 주장한 `commandSequence`는 정규화 요청 JSON에 항상 저장한다.
- `accepted_command_sequence` 열은 성공 명령만 채우고 거부 결과는 null이다.
- `(room_id, player_id, accepted_command_sequence)`에는 null이 아닌 행만 대상으로 하는 부분 고유 인덱스를 둔다.
- 따라서 쿨다운 거부 뒤 새 requestId와 같은 sequence로 정상 재시도할 수 있지만, 이미 성공한 sequence는 다시 성공할 수 없다.

## 6. 검증 순서

검증 순서는 결과의 일관성을 위해 고정한다.

```text
1. 기본 형식 검사
2. Room 존재 확인
3. JWT Player가 Room 참가자인지 권한 검사
4. 기존 requestId 결과 조회
5. Room 생명주기 검사
6. 현재 연결 상태와 connectionId·connectionGeneration 검사
7. Player 전투 가능 상태 검사
8. commandSequence 검사
9. KnownStateVersion 검사
10. 서버 시간과 쿨다운 검사
11. 피해·적 반격·웨이브 전이 계산
12. 후보 상태와 최초 결과 DB 저장
13. Commit 후 Grain 메모리 상태 교체
```

Room 존재와 참가 권한은 저장된 결과를 보여주기 전에 검사한다. 그래야 다른 Player가 알아낸 requestId로 결과를 읽을 수 없다. 권한을 확인한 뒤에는 기존 requestId 확인을 현재 연결·명령 순번보다 먼저 수행해 응답 유실 뒤 같은 요청의 최초 결과를 재생한다.

## 7. stateVersion 정책

`KnownStateVersion`은 선택 값으로 둔다.

- null이면 버전 비교 없이 나머지 서버 규칙을 검사한다.
- 음수이거나 서버 버전보다 크면 `InvalidKnownStateVersion`으로 거부한다.
- 첫 구현의 BasicAttack·UseSkill은 특정 적 ID나 위치를 지정하지 않는 의도 명령이므로 서버 버전보다 작아도 처리한다.
- 처리 결과에는 항상 최신 `StateVersion`과 스냅샷을 반환한다.
- 향후 특정 버전의 대상·아이템을 전제로 하는 명령이 생기면 그 명령만 `StaleState`로 거부한다.

첫 구현에서는 `StaleState`가 실제 BasicAttack·UseSkill에서 발생하지 않는다. `commandSequence`와 상태 머신을 최종 규칙으로 사용하고,
`KnownStateVersion`은 오류 진단과 클라이언트 동기화 보조 정보로만 사용한다.

`StateVersion`은 방 Create Commit 뒤 1로 시작한다. 상태를 바꾸는 명령·연결 판정 한 건이 Commit될 때 정확히 1 증가하고,
거부·재생·조회에는 증가하지 않는다. 한 명령에서 피해·반격·웨이브 전환 이벤트가 여러 개 발생해도 증가량은 1이다.

## 8. 서버 시간 추상화

직접 `DateTimeOffset.UtcNow`를 여러 위치에서 호출하지 않고 .NET의 `TimeProvider`를 주입한다.

```csharp
public sealed class GameRoomGrain(
    IDbContextFactory<GameDbContext> dbContextFactory,
    TimeProvider timeProvider)
```

운영 환경은 `TimeProvider.System`을 사용한다.

```csharp
var now = timeProvider.GetUtcNow();
```

한 명령을 시작할 때 `GetUtcNow()`를 정확히 한 번 호출하고 그 값을 순수 상태 전이와 영속 기록에 전달한다.
다음 모든 시각은 같은 TimeProvider에서 얻는다.

- 방 `StartedAt`·`CompletedAt`
- 요청 기록 `CreatedAt`
- 공격·스킬 `ReadyAt`
- 연결 `LastSeenAt`·`LeaseExpiresAt`·`DisconnectedAt`·`ReconnectDeadline`·`AbandonedAt`
- Room·요청의 `CreatedAt`과 `InitialConnectDeadline`
- 게임 결과·후처리 상태 변경 시각

현재 `GameRoomState` 내부의 정적 팩터리도 직접 UTC 시각을 읽지 않고 `now`를 매개변수로 받는다.
Silo 실행 프로젝트의 DI(Dependency Injection, 의존성 주입)에는 `TimeProvider.System`을 Singleton(단일 인스턴스)으로 등록한다.

테스트에서는 시간을 직접 전진시킬 수 있는 가짜 TimeProvider를 사용한다.

```text
현재 시각 12:00:00
스킬 사용 → SkillReadyAt 12:00:05
가짜 시간 4초 전진 → 스킬 거부
가짜 시간 1초 추가 전진 → 스킬 허용
```

실제 `Task.Delay`로 5초 기다리는 테스트는 느리고 불안정하므로 사용하지 않는다.

통합 테스트 Silo에는 조절 가능한 `ManualTimeProvider`를 Singleton으로 등록한다. 테스트 전용 시간 제어 경로로 시각을 전진시키며,
공유 TestCluster 테스트는 직렬 실행하고 각 테스트 시작 때 기준 시각을 초기화한다. Silo 재시작 시 Fixture가 마지막 가짜 UTC 시각을
새 Provider에 다시 주입하여 DB의 절대 ReadyAt과 같은 시간축을 유지한다. 순수 상태 단위 테스트는 Provider 자체가 아니라 명령별 `now` 값을 직접 전달한다.

## 9. 쿨다운 표현

쿨다운은 남은 초가 아니라 다음 사용 가능 절대 시각으로 저장한다.

```csharp
DateTimeOffset? BasicAttackReadyAt;
DateTimeOffset? SkillReadyAt;
```

허용 조건:

```text
ReadyAt이 null이거나 serverNow >= ReadyAt
```

성공 시:

```text
BasicAttackReadyAt = serverNow + 기본 공격 쿨다운
SkillReadyAt       = serverNow + 스킬 쿨다운
```

절대 UTC 시각을 저장하면 Silo 재시작 뒤에도 남은 쿨다운을 다시 계산할 수 있다.

## 10. 최소 전투 수치

권장 초기값:

| 명령 | 피해량 | 쿨다운 |
|---|---:|---:|
| 일반 공격 | 20 | 1초 |
| 스킬 | 50 | 5초 |

수치는 `CombatRuleSet`이라는 서버 정책 객체에 둔다.

```csharp
public sealed record CombatRuleSet(
    int BasicAttackDamage,
    TimeSpan BasicAttackCooldown,
    int SkillDamage,
    TimeSpan SkillCooldown);
```

클라이언트 요청이나 `appsettings.json` 변경만으로 운영 중 수치가 임의 변경되지 않게 첫 구현은 코드 상수로 고정한다. 데이터 기반 게임 설정은 후속 생산성 도구 범위에서 검토한다.

## 11. 명령 적용 알고리즘

```text
ExecuteAction(command):
    if requestId가 저장되어 있음:
        본문이 같으면 최초 결과 재생
        본문이 다르면 RequestIdConflict

    Room과 Player 상태 검증

    if connectionId 또는 connectionGeneration이 현재 연결과 다름:
        StaleConnection

    if commandSequence != lastSequence + 1:
        더 작으면 CommandSequenceAlreadyPassed
        더 크면 CommandSequenceGap

    readyAt = 명령 종류의 다음 사용 가능 시각
    if serverNow < readyAt:
        CooldownActive와 readyAt 반환

    후보 상태 복제
    후보 상태에 서버 피해량 적용
    쿨다운 readyAt 갱신
    lastSequence 갱신
    적 반격·웨이브·완료 상태 계산
    최초 결과 한 행과 후보 상태 변경분을 DB Transaction으로 저장
    Commit 성공 후 후보 상태를 현재 메모리 상태로 교체
```

## 12. 쿨다운 거부와 멱등성 저장

쿨다운 중 거부 결과는 저장 여부를 명확히 정해야 한다.

권장 정책:

- 유효한 requestId로 들어온 도메인 거부 결과도 `game_room_requests`에 저장한다.
- 같은 requestId 재전송에는 최초 `CooldownActive` 결과를 재생한다.
- 새 requestId와 같은 commandSequence로 쿨다운 종료 뒤 다시 시도할 수 있다.
- 거부 명령은 `LastAcceptedCommandSequence`를 증가시키지 않는다.
- 거부 행의 `accepted_command_sequence`는 null이므로 승인 순번 부분 고유 인덱스를 차지하지 않는다.

이 정책은 같은 requestId의 결과가 시간 경과에 따라 거부에서 성공으로 바뀌는 문제를 막는다.

## 13. 오래된 순번 재시도 구분

예시:

```text
명령 1 성공
명령 2 성공했지만 응답 유실
클라이언트가 명령 2를 같은 requestId로 재시도
```

서버는 requestId를 먼저 확인해 명령 2의 성공 결과를 재생한다.

반면 다음 요청은 잘못된 새 요청이다.

```text
명령 2가 이미 성공
새 requestId로 commandSequence 2 전송
```

이 경우 `CommandSequenceAlreadyPassed`로 거부하고 현재 마지막 순번과 최신 스냅샷을 반환한다.

## 14. 연결 상태와 명령

- `Connected` Player만 새 공격·스킬 명령을 보낼 수 있다.
- 요청의 `connectionId`와 `connectionGeneration`이 현재 연결과 모두 일치해야 한다.
- `Disconnected` Player의 새 명령은 거부한다.
- 새 Reconnect가 성공해 generation이 증가하면 이전 connectionId·generation 조합의 명령은 `StaleConnection`으로 거부한다.
- 연결이 끊기기 전에 처리된 requestId의 재생 조회는 허용할 수 있다.
- 재접속 성공 뒤에는 이전 `LastAcceptedCommandSequence + 1`부터 이어간다.
- 재접속한다고 쿨다운을 초기화하지 않는다.

## 15. 오류 결과

```csharp
public sealed record GameRoomActionResult(
    bool IsReplay,
    GameRoomActionError Error,
    long StateVersion,
    long LastAcceptedCommandSequence,
    DateTimeOffset? RetryAt,
    GameRoomCombatSnapshot? Room,
    CombatEvent[] Events);
```

`RetryAt`은 `CooldownActive`처럼 시간이 지나면 다시 시도할 수 있는 오류에만 설정한다.

### 15.1 HTTP 상태 코드 권장 매핑

| Grain 오류 | HTTP | 의미 |
|---|---:|---|
| InvalidRequestId / InvalidCommandSequence | 400 | 요청 형식 오류 |
| InvalidKnownStateVersion / InvalidConnectionId / InvalidConnectionGeneration | 400 | 요청 값 오류 |
| PlayerNotInRoom | 403 | 방 명령 권한 없음 |
| RoomNotCreated | 404 | 방 없음 |
| RequestIdConflict | 409 | 같은 멱등성 키의 다른 본문 |
| CommandSequenceAlreadyPassed / CommandSequenceGap | 409 | 명령 순서 충돌 |
| CommandSequenceExhausted | 409 | 명령 순번 범위 소진 |
| StaleConnection / PlayerDisconnected | 409 | 현재 활성 연결에서 온 명령이 아님 |
| StaleState | 409 | 버전 고정이 필요한 향후 명령의 오래된 상태 |
| CooldownActive | 409 | 현재 상태에서 아직 사용 불가 |
| RoomNotInGame / RoomCompleted | 409 | 방 상태 충돌 |

첫 구현에서는 `429 Too Many Requests`를 쿨다운 오류로 사용하지 않는다. HTTP Rate Limiting(호출 빈도 제한)과 게임 규칙 쿨다운은 서로 다른 개념이기 때문이다.

## 16. 오버플로와 경계값

- 피해량은 0보다 커야 한다.
- 쿨다운은 0보다 커야 한다.
- 적 체력 감소는 0 아래로 내려가지 않게 고정한다.
- Player 체력 감소도 0 아래로 내려가지 않게 고정한다.
- `commandSequence`는 1 이상이어야 한다.
- `connectionId`는 빈 Guid가 아니어야 하고 `connectionGeneration`은 1 이상이어야 한다.
- `KnownStateVersion`은 null이거나 0 이상이어야 한다.
- `long.MaxValue`에 도달하면 더 이상 증가시키지 않고 명시적 오류로 처리한다.
- `StateVersion`, `EnemyAttackSequence`, `connectionGeneration`도 `long.MaxValue`에서 증가가 필요한 명령을 적용하지 않는다.
- `TimeSpan` 덧셈 오버플로는 서버 설정 오류로 간주하고 명령을 적용하지 않는다.

## 17. DB 기록

전투 명령 요청 본문에는 다음 값을 저장한다.

```json
{
  "playerId": "...",
  "connectionId": "...",
  "connectionGeneration": 3,
  "commandSequence": 12,
  "actionKind": "UseSkill",
  "knownStateVersion": 35
}
```

결과에는 다음 정보를 저장한다.

```json
{
  "error": "None",
  "stateVersion": 36,
  "lastAcceptedCommandSequence": 12,
  "retryAt": null,
  "events": []
}
```

저장된 요청 본문 전체를 비교해 같은 requestId의 다른 내용 재사용을 차단한다.

비교 대상은 외부 HTTP의 원시 JSON 문자열이 아니라 API가 만든 내부 `ExecuteGameRoomActionCommand`를
고정된 속성 순서·열거형 문자열·null 표현으로 직렬화한 Canonical Payload(정규화 본문)다.
공백·JSON 속성 순서 차이만으로 충돌하지 않지만, connectionId·generation·sequence·actionKind·knownStateVersion 중
하나라도 의미상 다르면 `RequestIdConflict`다.

`game_room_requests` 저장 규칙은 다음과 같다.

- 기본 키: `(room_id, request_id)`
- 성공한 전투 명령: `player_id`와 `accepted_command_sequence`를 모두 저장
- 도메인 거부: `player_id`는 저장할 수 있으나 `accepted_command_sequence`는 null
- 형식 검사조차 통과하지 못한 빈 requestId는 저장하지 않음
- 기존 Legacy Start·Complete는 payload null, 새 Create·StartCombat·BasicAttack·UseSkill·Cancel·Connect·Reconnect·Disconnect는 payload 필수
- Start API 동작은 호출자의 connectionId·generation이 포함된 새 `StartCombat` 저장 종류를 사용
- Heartbeat는 요청 이력에 저장하지 않고 기존 Player Lease 행만 Update
- 현재 CHECK Constraint는 같은 Migration에서 위 명령 집합에 맞게 교체
- 새 요청 한 건만 추가하고 기존 방의 요청 행 전체를 삭제·재삽입하지 않음

## 18. 테스트 계획

### 18.1 명령 순번

- 첫 명령 순번 1 성공
- 0과 음수 거부
- 같은 requestId 재전송 결과 재생
- 같은 순번·새 requestId 거부
- 순번 건너뛰기 거부
- 네 Player의 순번은 서로 독립
- 쿨다운 거부 행 저장 뒤 새 requestId·같은 순번 성공
- 성공한 같은 순번을 다른 requestId로 보내면 부분 고유 인덱스가 최종 차단

### 18.2 서버 시간과 쿨다운

- 일반 공격 직후 재사용 거부
- 가짜 시간 1초 전진 뒤 일반 공격 허용
- 스킬 4.999초 뒤 거부
- 스킬 5초 뒤 허용
- Silo 재시작 뒤 저장된 ReadyAt 유지
- 클라이언트 시각을 조작해도 영향 없음
- StartedAt·CompletedAt·request CreatedAt·ReadyAt이 모두 같은 가짜 시간축 사용
- Silo 재시작 뒤 ManualTimeProvider가 마지막 테스트 UTC 시각에서 재개

### 18.3 멱등성과 상태

- 같은 requestId·같은 명령 재생
- 같은 requestId·다른 ActionKind 충돌
- Ready 상태 공격 거부 결과 재생
- Completed 상태 공격 거부
- 쿨다운 거부는 순번을 소비하지 않음
- DB Commit 실패 시 순번·체력·쿨다운 미반영

### 18.4 보안

- JWT Player와 내부 명령 Player 불일치 경로가 존재하지 않음
- 비참가자 공격 403
- 요청에 피해량·보상 수량 필드가 없음
- Disconnected Player 새 명령 거부
- 최신 JWT라도 이전 connectionId 또는 generation이면 StaleConnection
- 새 connectionId와 이전 generation, 이전 connectionId와 새 generation 조합 모두 거부

## 19. 검토한 대안

### 19.1 클라이언트 시각 사용

- 장점: 서버가 시간을 계산하지 않아도 된다.
- 단점: 기기 시계 조작과 네트워크 지연에 취약하다.
- 결정: 사용하지 않는다.

### 19.2 requestId만 사용하고 commandSequence 생략

- 장점: 계약이 단순하다.
- 단점: 각각 고유한 지연 명령들의 순서를 판정할 수 없다.
- 결정: 두 값을 함께 사용한다.

### 19.3 거부된 쿨다운 요청을 저장하지 않음

- 장점: DB 기록이 줄어든다.
- 단점: 같은 requestId가 시간이 지난 뒤 성공해 멱등성이 깨진다.
- 결정: 도메인 거부 결과도 저장한다.

### 19.4 쿨다운에 HTTP 429 사용

- 장점: 너무 빠른 요청이라는 의미가 비슷해 보인다.
- 단점: 인프라 호출 제한과 게임 상태 규칙이 섞인다.
- 결정: 상태 충돌인 409와 구체적인 도메인 오류를 사용한다.

## 20. 구현 순서

1. `GameRoomActionKind`, 명령·결과·통합 오류 계약 작성
2. Silo `TimeProvider.System` DI와 테스트용 ManualTimeProvider 제어 경로 준비
3. Player별 connectionId·generation·순번·ReadyAt 상태 추가
4. 순수 검증 순서 단위 테스트
5. 일반 공격 적용
6. 스킬 적용
7. 승인 순번 부분 고유 인덱스·payload CHECK Migration 작성
8. DB 요청 결과 한 행과 Player 상태 변경분을 증분 영속화
9. HTTP API와 JWT Player·현재 연결 검증 연결
10. Silo 재시작·가짜 시간·중복·역순 통합 테스트

## 21. 설계 확정안

1. 일반 공격 피해 20·쿨다운 1초를 사용한다.
2. 스킬 피해 50·쿨다운 5초를 사용한다.
3. Player의 첫 commandSequence는 1이다.
4. 쿨다운을 포함한 도메인 거부 결과도 requestId 기록으로 저장한다.
5. 거부 행은 accepted_command_sequence를 null로 두고 성공 행만 부분 고유 인덱스에 포함한다.
6. 쿨다운 오류는 HTTP 409로 반환한다.
7. `KnownStateVersion`은 선택 값이며 첫 BasicAttack·UseSkill에서는 진단용으로만 사용한다.
8. 모든 새 전투 명령은 JWT Player뿐 아니라 현재 connectionId·generation까지 검증한다.
9. 모든 방 관련 UTC 시각은 주입된 TimeProvider 하나에서 읽는다.

이 값을 사용해 명령 검증 구조를 먼저 증명하고 밸런스 변경은 후속 범위로 둔다.

## 22. 완료 기준

- 클라이언트와 서버가 결정하는 값이 명확히 분리되어 있다.
- requestId·commandSequence·stateVersion의 역할을 각각 설명할 수 있다.
- 쿨다운이 서버 UTC 시각으로 판정된다.
- 실제 대기 없이 가짜 시간으로 테스트할 수 있다.
- 거부된 명령의 순번과 멱등성 처리 원칙이 모호하지 않다.
- 재접속 뒤 이전 연결의 전투 명령이 차단된다.
- 거부 요청 저장과 성공 순번 고유 제약이 서로 충돌하지 않는다.
- 요청 한 건 저장 시 기존 요청 이력을 다시 쓰지 않는다.
- Silo 재시작 뒤에도 명령 순번과 쿨다운이 유지된다.
