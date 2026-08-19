namespace CoopGameServer.Persistence.Parties;

/// <summary>
/// PostgreSQL의 party_requests 테이블에 저장되는 파티 명령의 최초 처리 결과입니다.
/// </summary>
/// <remarks>
/// 같은 requestId가 다시 전달되면 이 행을 읽어 최초 결과를 반환합니다.
/// 따라서 Silo가 재시작되어 메모리가 비워져도 멱등성(Idempotency, 같은 요청을 반복해도 결과가 달라지지 않는 성질)이 유지됩니다.
/// </remarks>
public sealed class PartyRequestRecord
{
    private PartyRequestRecord()
    {
    }

    /// <summary>
    /// 명령 본문과 그 명령을 처음 처리했을 때의 반환 결과를 저장할 행을 만듭니다.
    /// </summary>
    public PartyRequestRecord(
        Guid requestId,
        Guid partyId,
        string commandKind,
        Guid? playerId,
        Guid? roomId,
        int resultError,
        int? resultLifecycle,
        Guid? resultLeaderPlayerId,
        Guid[]? resultMemberPlayerIds,
        Guid? resultCurrentRoomId,
        DateTimeOffset createdAt)
    {
        RequestId = requestId;
        PartyId = partyId;
        CommandKind = commandKind;
        PlayerId = playerId;
        RoomId = roomId;
        ResultError = resultError;
        ResultLifecycle = resultLifecycle;
        ResultLeaderPlayerId = resultLeaderPlayerId;
        ResultMemberPlayerIds = resultMemberPlayerIds;
        ResultCurrentRoomId = resultCurrentRoomId;
        CreatedAt = createdAt;
    }

    /// <summary>전체 파티 명령에서 중복되지 않아야 하는 멱등성 키입니다.</summary>
    public Guid RequestId { get; private set; }

    /// <summary>명령 대상 PartyGrain의 식별자입니다.</summary>
    public Guid PartyId { get; private set; }

    /// <summary>파티 생성·멤버 변경·매칭·게임 상태 전이 중 하나인 명령 종류입니다.</summary>
    public string CommandKind { get; private set; } = string.Empty;

    /// <summary>리더 또는 멤버를 대상으로 하는 명령의 플레이어 식별자입니다.</summary>
    public Guid? PlayerId { get; private set; }

    /// <summary>게임 시작·완료 명령의 게임 방 식별자입니다.</summary>
    public Guid? RoomId { get; private set; }

    /// <summary>최초 응답의 PartyCommandError 값을 정수로 저장합니다.</summary>
    public int ResultError { get; private set; }

    /// <summary>최초 응답에 파티가 있었다면 그때의 PartyLifecycle 값을 저장합니다.</summary>
    public int? ResultLifecycle { get; private set; }

    /// <summary>최초 응답 시점의 리더이며, 파티가 없거나 해산 상태라면 null입니다.</summary>
    public Guid? ResultLeaderPlayerId { get; private set; }

    /// <summary>최초 응답 시점의 가입 순서가 보존된 멤버 배열입니다.</summary>
    public Guid[]? ResultMemberPlayerIds { get; private set; }

    /// <summary>최초 응답 시점에 파티가 참가 중이던 게임 방 식별자입니다.</summary>
    public Guid? ResultCurrentRoomId { get; private set; }

    /// <summary>명령을 최초 처리한 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
