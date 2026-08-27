namespace CoopGameServer.GrainContracts.GameRooms;

/// <summary>GameRoomGrain 명령이 거부된 이유입니다.</summary>
public enum GameRoomCommandError
{
    /// <summary>명령이 정상 처리됐습니다.</summary>
    None = 0,

    /// <summary>멱등성 요청 식별자가 비어 있습니다.</summary>
    InvalidRequestId = 1,

    /// <summary>방 식별자가 비어 있거나 Grain 기본 키와 일치하지 않습니다.</summary>
    InvalidRoomId = 2,

    /// <summary>매칭 조건 키가 비어 있거나 허용 길이를 넘었습니다.</summary>
    InvalidQueueKey = 3,

    /// <summary>사전 구성 파티 식별자 배열의 형태가 올바르지 않습니다.</summary>
    InvalidPartyIds = 4,

    /// <summary>참가 플레이어가 정확히 4명의 고유하고 유효한 식별자로 구성되지 않았습니다.</summary>
    InvalidPlayerIds = 5,

    /// <summary>이미 생성된 방에 다시 생성 명령을 보냈습니다.</summary>
    RoomAlreadyExists = 6,

    /// <summary>아직 생성되지 않은 방에 시작 또는 종료 명령을 보냈습니다.</summary>
    RoomNotCreated = 7,

    /// <summary>이미 게임이 시작된 방에 다시 시작 명령을 보냈습니다.</summary>
    RoomAlreadyStarted = 8,

    /// <summary>게임을 시작하지 않은 방에 종료 명령을 보냈습니다.</summary>
    RoomNotInGame = 9,

    /// <summary>완료된 방에 다시 상태 변경 명령을 보냈습니다.</summary>
    RoomCompleted = 10,

    /// <summary>같은 requestId가 이전과 다른 명령 또는 내용으로 재사용됐습니다.</summary>
    RequestIdConflict = 11,

    /// <summary>연결된 사전 구성 파티가 방 상태 전이를 받아들일 수 없는 상태입니다.</summary>
    PartyTransitionFailed = 12,

    /// <summary>PartyGrain의 실제 멤버가 MatchAssignment의 4인 참가자에 포함되지 않습니다.</summary>
    PartyRosterMismatch = 13,

    /// <summary>완료 결과가 Victory·Defeat·Cancelled 중 하나가 아닙니다.</summary>
    InvalidOutcome = 14,
}
