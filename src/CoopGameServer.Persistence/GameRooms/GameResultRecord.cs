namespace CoopGameServer.Persistence.GameRooms;

/// <summary>
/// 완료된 한 게임에서 플레이어 한 명에게 결과 보상을 전달하는 진행 상태를 저장합니다.
/// </summary>
/// <remarks>
/// 기본 키는 (RoomId, PlayerId)입니다. 따라서 같은 게임의 같은 플레이어 결과가
/// 재시도나 Silo 재시작 때문에 두 행으로 나뉘지 않습니다.
/// </remarks>
public sealed class GameResultRecord
{
    /// <summary>DB 오류 코드가 저장될 수 있는 최대 길이입니다.</summary>
    public const int MaxLastErrorCodeLength = 100;

    /// <summary>EF Core 전용 생성자입니다.</summary>
    private GameResultRecord()
    {
    }

    /// <summary>방 완료 Transaction 안에서 최초 Pending 결과 행을 만듭니다.</summary>
    public GameResultRecord(
        Guid roomId,
        Guid playerId,
        int rewardPolicyVersion,
        Guid rewardRequestId,
        DateTimeOffset updatedAt)
    {
        RoomId = roomId;
        PlayerId = playerId;
        RewardPolicyVersion = rewardPolicyVersion;
        RewardRequestId = rewardRequestId;
        DeliveryStatus = GameResultDeliveryStatus.Pending;
        AttemptCount = 0;
        UpdatedAt = updatedAt;
    }

    /// <summary>완료된 게임 방 식별자이며 복합 기본 키의 첫 번째 부분입니다.</summary>
    public Guid RoomId { get; private set; }

    /// <summary>결과를 전달받을 플레이어 식별자이며 복합 기본 키의 두 번째 부분입니다.</summary>
    public Guid PlayerId { get; private set; }

    /// <summary>방 생성 시점에 고정된 보상 정책 버전입니다.</summary>
    public int RewardPolicyVersion { get; private set; }

    /// <summary>PlayerGrain과 reward_audits에서 재사용할 결정적 멱등성 요청 식별자입니다.</summary>
    public Guid RewardRequestId { get; private set; }

    /// <summary>미전달·재시도·적용·무보상·영구 실패 중 현재 전달 상태입니다.</summary>
    public GameResultDeliveryStatus DeliveryStatus { get; private set; }

    /// <summary>PlayerGrain 전달을 실제로 시도한 횟수입니다.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>일시 장애 뒤 다음 자동 재시도가 가능해지는 UTC 시각입니다.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>마지막 전달 실패의 구조화된 오류 코드이며 정상 상태에서는 null입니다.</summary>
    public string? LastErrorCode { get; private set; }

    /// <summary>이 결과 행의 상태를 마지막으로 변경한 UTC 시각입니다.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }
}
