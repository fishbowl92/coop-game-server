namespace CoopGameServer.Persistence.GameRooms;

/// <summary>GameRoomGrain 명령의 최초 요청과 결과를 JSON으로 보관하는 멱등성 행 모델입니다.</summary>
public sealed class GameRoomRequestRecord
{
    /// <summary>EF Core 전용 생성자입니다.</summary>
    private GameRoomRequestRecord()
    {
    }

    /// <summary>새 게임 방 요청 처리 기록을 만듭니다.</summary>
    public GameRoomRequestRecord(
        Guid requestId,
        Guid roomId,
        string commandKind,
        string? requestPayloadJson,
        string resultPayloadJson,
        DateTimeOffset createdAt,
        Guid? playerId = null,
        long? acceptedCommandSequence = null)
    {
        RequestId = requestId;
        RoomId = roomId;
        CommandKind = commandKind;
        RequestPayloadJson = requestPayloadJson;
        ResultPayloadJson = resultPayloadJson;
        CreatedAt = createdAt;
        PlayerId = playerId;
        AcceptedCommandSequence = acceptedCommandSequence;
    }

    /// <summary>같은 GameRoom 안에서 명령 재시도를 식별하는 멱등성 키입니다.</summary>
    public Guid RequestId { get; private set; }

    /// <summary>요청을 처리한 GameRoomGrain의 방 식별자입니다.</summary>
    public Guid RoomId { get; private set; }

    /// <summary>Create·Start·Complete·BasicAttack·UseSkill 중 하나의 명령 이름입니다.</summary>
    public string CommandKind { get; private set; } = string.Empty;

    /// <summary>배정 정보·완료 결과 또는 전투 명령 원문 JSON입니다. Start 명령만 null입니다.</summary>
    public string? RequestPayloadJson { get; private set; }

    /// <summary>최초 결과 JSON입니다. 전투는 GameRoomCombatResult, 나머지는 GameRoomCommandResult 형식을 사용합니다.</summary>
    public string ResultPayloadJson { get; private set; } = string.Empty;

    /// <summary>최초 결과를 기록한 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>전투 명령을 요청한 참가자입니다. 생명주기 명령은 null입니다.</summary>
    public Guid? PlayerId { get; private set; }

    /// <summary>성공한 전투 순번만 저장합니다. 거부는 null이므로 같은 순번으로 새 요청을 보낼 수 있습니다.</summary>
    public long? AcceptedCommandSequence { get; private set; }
}
