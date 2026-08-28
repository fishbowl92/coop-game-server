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

    /// <summary>실제 보상이 적용됐거나 기존 적용 결과를 재확인한 상태로 확정합니다.</summary>
    /// <param name="updatedAt">PlayerGrain 응답을 저장하는 UTC 서버 시각입니다.</param>
    public void MarkApplied(DateTimeOffset updatedAt)
    {
        CompleteAttempt(GameResultDeliveryStatus.Applied, updatedAt);
    }

    /// <summary>정책상 지급할 보상이 없는 정상 상태로 확정합니다.</summary>
    /// <param name="updatedAt">PlayerGrain 응답을 저장하는 UTC 서버 시각입니다.</param>
    public void MarkNoReward(DateTimeOffset updatedAt)
    {
        CompleteAttempt(GameResultDeliveryStatus.NoReward, updatedAt);
    }

    /// <summary>자동 재시도로 해결할 수 없는 업무 오류를 기록하고 전달을 종료합니다.</summary>
    /// <param name="errorCode">PlayerGrain이 반환한 구조화된 업무 오류 이름입니다.</param>
    /// <param name="updatedAt">오류를 저장하는 UTC 서버 시각입니다.</param>
    public void MarkTerminalFailure(string errorCode, DateTimeOffset updatedAt)
    {
        EnsureDeliveryCanBeAttempted();
        var normalizedErrorCode = NormalizeErrorCode(errorCode);

        DeliveryStatus = GameResultDeliveryStatus.TerminalFailure;
        AttemptCount = checked(AttemptCount + 1);
        NextAttemptAt = null;
        LastErrorCode = normalizedErrorCode;
        UpdatedAt = updatedAt;
    }

    /// <summary>일시적인 기반시설 장애를 기록하고 다음 자동 재시도 시각을 예약합니다.</summary>
    /// <param name="errorCode">예외 형식처럼 운영자가 원인을 분류할 수 있는 짧은 코드입니다.</param>
    /// <param name="nextAttemptAt">이 시각 이후에만 다시 전달할 수 있습니다.</param>
    /// <param name="updatedAt">현재 실패를 저장하는 UTC 서버 시각입니다.</param>
    public void ScheduleRetry(
        string errorCode,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset updatedAt)
    {
        EnsureDeliveryCanBeAttempted();

        if (nextAttemptAt <= updatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAt),
                nextAttemptAt,
                "다음 재시도 시각은 현재 상태 변경 시각보다 뒤여야 합니다.");
        }

        var normalizedErrorCode = NormalizeErrorCode(errorCode);

        DeliveryStatus = GameResultDeliveryStatus.PendingRetry;
        AttemptCount = checked(AttemptCount + 1);
        NextAttemptAt = nextAttemptAt;
        LastErrorCode = normalizedErrorCode;
        UpdatedAt = updatedAt;
    }

    /// <summary>성공 또는 정상 무보상 상태에 공통으로 필요한 값을 변경합니다.</summary>
    private void CompleteAttempt(GameResultDeliveryStatus completedStatus, DateTimeOffset updatedAt)
    {
        EnsureDeliveryCanBeAttempted();

        if (completedStatus is not GameResultDeliveryStatus.Applied and
            not GameResultDeliveryStatus.NoReward)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStatus),
                completedStatus,
                "완료 상태는 Applied 또는 NoReward여야 합니다.");
        }

        DeliveryStatus = completedStatus;
        AttemptCount = checked(AttemptCount + 1);
        NextAttemptAt = null;
        LastErrorCode = null;
        UpdatedAt = updatedAt;
    }

    /// <summary>최종 상태를 실수로 다시 변경하여 완료 결과를 훼손하지 못하게 막습니다.</summary>
    private void EnsureDeliveryCanBeAttempted()
    {
        if (DeliveryStatus is not GameResultDeliveryStatus.Pending and
            not GameResultDeliveryStatus.PendingRetry)
        {
            throw new InvalidOperationException(
                $"최종 전달 상태 {DeliveryStatus}에서는 새로운 전달 결과를 기록할 수 없습니다.");
        }
    }

    /// <summary>DB 열 길이 안에서 공백이 아닌 구조화된 오류 코드만 허용합니다.</summary>
    private static string NormalizeErrorCode(string errorCode)
    {
        var normalizedErrorCode = errorCode?.Trim();

        if (string.IsNullOrEmpty(normalizedErrorCode) ||
            normalizedErrorCode.Length > MaxLastErrorCodeLength)
        {
            throw new ArgumentException(
                $"오류 코드는 1자 이상 {MaxLastErrorCodeLength}자 이하여야 합니다.",
                nameof(errorCode));
        }

        return normalizedErrorCode;
    }
}
