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
        DateTimeOffset createdAt)
    {
        RequestId = requestId;
        RoomId = roomId;
        CommandKind = commandKind;
        RequestPayloadJson = requestPayloadJson;
        ResultPayloadJson = resultPayloadJson;
        CreatedAt = createdAt;
    }

    /// <summary>클라이언트 재전송을 식별하는 전역 고유 요청 번호입니다.</summary>
    public Guid RequestId { get; private set; }

    /// <summary>요청을 처리한 GameRoomGrain의 방 식별자입니다.</summary>
    public Guid RoomId { get; private set; }

    /// <summary>Create·Start·Complete 중 하나의 명령 이름입니다.</summary>
    public string CommandKind { get; private set; } = string.Empty;

    /// <summary>Create 명령의 MatchAssignment JSON이며, 매개변수가 없는 명령은 null입니다.</summary>
    public string? RequestPayloadJson { get; private set; }

    /// <summary>최초 GameRoomCommandResult를 직렬화한 JSON입니다.</summary>
    public string ResultPayloadJson { get; private set; } = string.Empty;

    /// <summary>최초 결과를 기록한 UTC 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
