namespace CoopGameServer.Contracts.Matchmaking;

/// <summary>매칭 대기열 명령이 성공했을 때 반환하는 외부 응답입니다.</summary>
/// <param name="IsReplay">같은 requestId의 최초 결과를 재생한 경우 true입니다.</param>
/// <param name="Ticket">요청한 참가 단위가 가진 현재 매칭 티켓입니다.</param>
/// <param name="Match">정확히 네 명이 모였다면 생성된 게임 방 배정이며, 아직 대기 중이면 null입니다.</param>
public sealed record MatchmakingResponse(
    bool IsReplay,
    MatchQueueTicketResponse Ticket,
    MatchAssignmentResponse? Match);

/// <summary>클라이언트가 조회할 수 있는 매칭 티켓의 현재 상태입니다.</summary>
public sealed record MatchQueueTicketResponse(
    Guid TicketId,
    string QueueKey,
    string EntryKind,
    Guid? PartyId,
    Guid LeaderPlayerId,
    Guid[] MemberPlayerIds,
    string Status,
    Guid? RoomId,
    DateTimeOffset EnqueuedAt,
    long QueueOrder);

/// <summary>정확히 네 명을 동일한 게임 방에 배정한 결과입니다.</summary>
public sealed record MatchAssignmentResponse(
    Guid RoomId,
    string QueueKey,
    Guid[] PartyIds,
    Guid[] PlayerIds,
    DateTimeOffset CreatedAt);
