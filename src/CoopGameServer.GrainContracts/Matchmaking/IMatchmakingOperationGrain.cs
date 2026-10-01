using CoopGameServer.GrainContracts.Parties;

namespace CoopGameServer.GrainContracts.Matchmaking;

/// <summary>한 외부 매칭 변경 요청을 영속 기록하고 여러 Grain의 단계를 끝까지 실행합니다.</summary>
public interface IMatchmakingOperationGrain : IGrainWithStringKey
{
    /// <summary>키와 본문이 같은 재전송은 최초 결과를 반환하며, 클라이언트 취소와 독립적으로 실행합니다.</summary>
    Task<MatchmakingOperationResult> ExecuteAsync(MatchmakingOperationRequest request);

    /// <summary>서버 복구 작업자가 저장된 본문으로 미완료 작업을 이어갑니다.</summary>
    Task ResumeAsync();
}

/// <summary>호출자 ID와 관리자 여부는 API에서 검증한 인증 정보만 전달합니다.</summary>
[GenerateSerializer]
public sealed record MatchmakingOperationRequest(
    [property: Id(0)] string QueueKey,
    [property: Id(1)] Guid RequestId,
    [property: Id(2)] MatchmakingOperationKind Kind,
    [property: Id(3)] Guid TargetId,
    [property: Id(4)] Guid RequesterPlayerId,
    [property: Id(5)] bool IsAdministrator)
{
    /// <summary>대기열과 외부 요청 ID를 함께 사용해 기존 멱등성 범위를 유지합니다.</summary>
    public string GetGrainKey() => $"{QueueKey}:{RequestId:N}";
}

public enum MatchmakingOperationKind { EnqueueParty = 0, Cancel = 1 }

/// <summary>기존 API 응답 오류의 의미를 유지하는 내부 조정 결과입니다.</summary>
public enum MatchmakingOperationError
{
    None = 0,
    PartyNotFound = 2,
    RequesterIsNotPartyLeader = 3,
    RequesterCannotManageTicket = 5,
    PartyTransitionFailed = 6,
    PartyCompensationFailed = 7,
}

[GenerateSerializer]
public sealed record MatchmakingOperationResult(
    [property: Id(0)] MatchmakingOperationError Error,
    [property: Id(1)] PartyCommandError? PartyError,
    [property: Id(2)] MatchQueueCommandResult? QueueResult);
