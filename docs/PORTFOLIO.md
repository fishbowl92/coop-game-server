# 포트폴리오 검토 안내

[저장소 소개로 돌아가기](../README.md)

C#과 .NET, Microsoft Orleans(오를리언스, 분산 가상 액터 프레임워크)로 동시성과 영속성을 학습하는 개인 프로젝트입니다. AI(Artificial Intelligence, 인공지능)를 구현·설명·검증 보조에 활용하고 있습니다. 모든 코드를 AI 도움 없이 직접 타이핑했다는 의미의 개인 프로젝트가 아닙니다.

요구사항과 제약을 정리하고, 제안된 설계의 이유를 확인하며, 자동 테스트와 설계 결정 문서를 대조하는 과정을 개인 학습·검증 범위로 제시합니다. 저장소의 설계·구현·테스트를 함께 확인할 수 있도록 대표 경로를 골랐습니다.

아래 코드 링크는 이 안내를 작성할 때 검토한 `f186650` 커밋에 고정되어 있습니다. 이후 구현 상태는 [최신 소개](../README.md)를 기준으로 확인해 주세요.

## 1. 보상 중복 처리와 동시 갱신

### 문제

네트워크 재시도로 같은 보상이 여러 번 반영되는 문제와, 서로 다른 정상 보상이 동시에 처리되어 수량 갱신이 누락되는 문제를 구분했습니다.

### 구현과 검증 경로

1. [멱등성 설계 결정](./adr/0003-use-idempotency-keys-for-state-changing-requests.md): 동일 요청의 재전송을 구분하는 키와 저장 경계.
2. [`PostgreSqlRewardWriter.WriteAsync`](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Persistence/Rewards/PostgreSqlRewardWriter.cs#L40-L151): 보상 이력·지갑·인벤토리를 하나의 트랜잭션(모두 성공하거나 모두 취소하는 작업 단위)으로 처리.
3. [`TryLockPlayerAsync`](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Persistence/Rewards/PostgreSqlRewardWriter.cs#L165-L183): 플레이어 행을 잠가 서로 다른 보상 요청의 갱신 경합 제어.
4. [동일 요청 100회 동시 처리 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Persistence/Rewards/PostgreSqlRewardWriterIntegrationTests.cs#L49-L89) · [서로 다른 동시 요청 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Persistence/Rewards/PostgreSqlRewardWriterIntegrationTests.cs#L96-L137).
5. [수량 초과 실패 시 롤백 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Persistence/Rewards/PostgreSqlRewardWriterIntegrationTests.cs#L270-L303): 일부 데이터만 남지 않는지 확인하는 테스트.

위 100회는 자동 테스트의 요청 수입니다. 동시 접속자 수용 능력이나 초당 처리량을 측정한 성능 지표가 아닙니다.

## 2. 4인 매칭과 재시작 복원

- [`MatchQueueGrain.EnqueueAsync`](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Grains/Matchmaking/MatchQueueGrain.cs#L74-L87): 등록 명령 처리와 매칭된 게임 방 준비.
- [큐 활성화 시 복원](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Grains/Matchmaking/MatchQueueGrain.cs#L29-L71): 저장된 티켓과 요청 처리 기록 복원.
- [파티를 쪼개지 않는 4인 조합 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/MatchQueueGrainTests.cs#L19-L78): 4명 파티, 3명+솔로, 2명+2명 조합.
- [취소와 매칭 경합 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/MatchQueueGrainTests.cs#L214-L249) · [재시작 후 티켓 복원 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Grains/Matchmaking/MatchQueueGrainTests.cs#L270-L291).

Grain(그레인)은 식별자를 기준으로 게임 상태와 명령을 맡는 단위입니다. [Orleans 선택 이유](./adr/0001-use-orleans-for-game-entity-coordination.md)에는 직접 잠금 관리와 액터 큐 구현을 대안으로 비교한 내용이 있습니다.

## 3. 게임 결과 전달 자동 복구

- [`GameRoomRecoveryProcessor.RecoverDueRoomsAsync`](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Grains/GameRooms/GameRoomRecoveryProcessor.cs#L29-L68): 미완료 방을 찾아 기존 완료 처리 경로에 다시 요청하며, 한 방의 실패가 다른 방 복구를 중단하지 않도록 처리.
- [미완료·재시도 시각 조회](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/src/CoopGameServer.Grains/GameRooms/GameRoomRecoveryProcessor.cs#L71-L90): 중복 방 번호를 제거하고 처리 개수 제한.
- [재시작 뒤 중복 보상 없이 복구하는 검사](https://github.com/fishbowl92/coop-game-server/blob/f186650b479674b67e5b5c87d2df1e88e428804c/tests/CoopGameServer.IntegrationTests/Grains/GameRooms/GameRoomRecoveryProcessorTests.cs#L21-L68).

현재는 단일 Silo(사일로, Orleans 서버 실행 프로세스)의 학습 범위입니다. 다중 서버 운영 안정성이나 장애 상황 전체를 입증한 것으로 표시하지 않습니다.

## AI 활용과 학습 자료를 읽는 방법

- 주차별 설계 문서와 구현 코드를 구분합니다. [4주차 설계 개요](./design/week-04-game-room-reconnect-overview.md)에 문서가 있다는 이유만으로 전투·재접속 구현이 끝난 것은 아닙니다.
- 설계 이유는 [ADR(Architecture Decision Record, 아키텍처 결정 기록)](./adr/0001-use-orleans-for-game-entity-coordination.md), 결과의 일관성은 위 테스트에서 확인할 수 있습니다.
- AI의 제안 자체를 성과로 삼기보다, 왜 이 경계를 선택했는지와 어떤 실패 조건을 검사하는지 설명하는 것을 목표로 합니다.

## 현재 범위와 확인 한계

인증, 보상, 파티, 4인 매칭, 게임 방의 최소 생명주기와 결과 전달을 구현한 학습 서버입니다. 실제 전투·재접속·Redis 애플리케이션 연동·운영 배포는 아직 구현하지 않았습니다.

이번 포트폴리오 편집에서는 코드와 테스트 정의를 대조했습니다. 서버 테스트 전체를 새로 실행했다거나 최신 실행 결과를 보증하는 문서가 아닙니다. 실행 방법은 [저장소의 처음 실행 안내](../README.md#처음-실행), 자동 실행 결과는 [GitHub Actions 검사 기록](https://github.com/fishbowl92/coop-game-server/actions)에서 확인할 수 있습니다.
