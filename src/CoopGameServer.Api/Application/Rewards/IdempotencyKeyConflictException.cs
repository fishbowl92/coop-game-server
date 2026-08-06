namespace CoopGameServer.Api.Application.Rewards;

/// <summary>
/// 이미 사용한 멱등성 키를 다른 보상 내용에 재사용했을 때 발생합니다.
/// </summary>
/// <remarks>
/// 같은 RequestId를 같은 요청의 네트워크 재시도에 쓰는 것은 정상입니다.
/// 그러나 다른 플레이어·골드·아이템·사유와 함께 재사용하면 어떤 보상을 의도했는지 판단할 수 없으므로
/// HTTP 409 Conflict로 거부해야 합니다.
/// </remarks>
public sealed class IdempotencyKeyConflictException : Exception
{
    /// <summary>
    /// 충돌한 멱등성 키를 포함하는 예외를 생성합니다.
    /// </summary>
    /// <param name="requestId">이미 다른 내용으로 사용된 멱등성 키입니다.</param>
    public IdempotencyKeyConflictException(Guid requestId)
        : base($"멱등성 키 '{requestId}'가 이미 다른 보상 요청에 사용되었습니다.")
    {
    }
}
