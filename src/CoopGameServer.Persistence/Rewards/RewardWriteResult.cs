namespace CoopGameServer.Persistence.Rewards;

/// <summary>PostgreSQL 보상 쓰기의 처리 결과입니다.</summary>
/// <remarks>
/// 연결 끊김, 명령 시간 초과, 숫자 Overflow(오버플로, 표현 범위를 넘는 연산) 같은 장애는
/// 업무 오류로 감추지 않고 예외로 전달합니다.
/// 생성자를 숨기고 의미가 드러나는 Factory Method(팩터리 메서드, 올바른 객체 생성을 전담하는 메서드)만
/// 제공하여 성공인데 영수증이 없거나, 실패인데 재생으로 표시되는 모순된 상태를 만들 수 없게 합니다.
/// </remarks>
public sealed record RewardWriteResult
{
    private RewardWriteResult(
        bool isReplay,
        RewardWriteError error,
        RewardWriteReceipt? receipt)
    {
        IsReplay = isReplay;
        Error = error;
        Receipt = receipt;
    }

    /// <summary>같은 requestId의 기존 성공 결과를 반환했다면 true입니다.</summary>
    public bool IsReplay { get; }

    /// <summary>예상 가능한 업무 오류이며 정상 결과에서는 None입니다.</summary>
    public RewardWriteError Error { get; }

    /// <summary>적용 또는 재생된 보상 영수증이며 정상 결과에서만 존재합니다.</summary>
    public RewardWriteReceipt? Receipt { get; }

    /// <summary>새 보상이 정상 적용된 결과를 만듭니다.</summary>
    /// <param name="receipt">DB에 새로 기록된 보상 영수증입니다.</param>
    public static RewardWriteResult Applied(RewardWriteReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new RewardWriteResult(isReplay: false, RewardWriteError.None, receipt);
    }

    /// <summary>같은 멱등성 키의 기존 성공 결과를 재생한 결과를 만듭니다.</summary>
    /// <param name="receipt">DB에서 다시 읽은 기존 보상 영수증입니다.</param>
    public static RewardWriteResult Replayed(RewardWriteReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new RewardWriteResult(isReplay: true, RewardWriteError.None, receipt);
    }

    /// <summary>DB를 변경하지 않은 예상 업무 오류 결과를 만듭니다.</summary>
    /// <param name="error">None이 아닌 정의된 업무 오류입니다.</param>
    /// <exception cref="ArgumentOutOfRangeException">None 또는 정의되지 않은 오류를 전달하면 발생합니다.</exception>
    public static RewardWriteResult Failed(RewardWriteError error)
    {
        if (error is RewardWriteError.None || !Enum.IsDefined(error))
        {
            throw new ArgumentOutOfRangeException(
                nameof(error),
                error,
                "실패 결과에는 None이 아닌 정의된 보상 쓰기 오류가 필요합니다.");
        }

        return new RewardWriteResult(isReplay: false, error, receipt: null);
    }
}
