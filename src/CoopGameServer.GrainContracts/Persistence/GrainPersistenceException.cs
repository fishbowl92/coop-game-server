namespace CoopGameServer.GrainContracts.Persistence;

/// <summary>드라이버 객체와 SQL 본문을 원격으로 보내지 않는 영속 처리 오류입니다.</summary>
[GenerateSerializer]
public sealed class GrainPersistenceException : Exception
{
    public GrainPersistenceException() { }
    public GrainPersistenceException(string message) : base(message) { }
    public GrainPersistenceException(string message, Exception innerException) : base(message, innerException) { }

    /// <param name="errorCode">비밀값이 없는 SQL 상태 코드 또는 일반 연결 오류 코드입니다.</param>
    /// <param name="isTransient">같은 멱등 요청을 재시도할 수 있는 일시 오류인지 나타냅니다.</param>
    public GrainPersistenceException(string errorCode, bool isTransient)
        : base("영속 처리에 실패했습니다. 서버의 오류 코드와 재시도 상태를 확인하세요.")
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
    }

    [Id(0)] public string ErrorCode { get; } = "Unknown";
    [Id(1)] public bool IsTransient { get; }
}
