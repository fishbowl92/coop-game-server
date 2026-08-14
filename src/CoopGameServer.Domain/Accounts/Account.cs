namespace CoopGameServer.Domain.Accounts;

/// <summary>
/// 로그인 식별자와 비밀번호 해시를 보관하는 계정 엔티티입니다.
/// </summary>
/// <remarks>
/// Account는 게임 캐릭터 자체가 아니라 "누가 이 Player를 조작할 수 있는가"를 나타냅니다.
/// 비밀번호 원문은 이 객체와 데이터베이스 어느 곳에도 저장하지 않고, 검증용 해시(hash)만 저장합니다.
/// </remarks>
public sealed class Account
{
    /// <summary>로그인 식별자의 최대 길이입니다.</summary>
    public const int MaxLoginIdLength = 30;

    /// <summary>
    /// EF Core(Entity Framework Core)가 DB 행을 객체로 복원할 때만 사용하는 생성자입니다.
    /// 일반 코드에서는 공개 생성자를 통해 규칙을 검증합니다.
    /// </summary>
    private Account()
    {
    }

    /// <summary>
    /// 로그인할 수 있는 새 계정의 기본 정보를 만듭니다.
    /// </summary>
    /// <param name="id">계정을 영구적으로 구별하는 Guid 식별자입니다.</param>
    /// <param name="playerId">이 계정이 조작할 Player의 식별자입니다.</param>
    /// <param name="loginId">로그인에 사용할 영문·숫자·밑줄 식별자입니다.</param>
    /// <param name="role">API 인가에 사용할 계정 역할입니다.</param>
    /// <param name="createdAt">UTC 기준 생성 시각입니다.</param>
    public Account(
        Guid id,
        Guid playerId,
        string loginId,
        AccountRole role,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("계정 식별자는 비어 있을 수 없습니다.", nameof(id));
        }

        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("플레이어 식별자는 비어 있을 수 없습니다.", nameof(playerId));
        }

        Id = id;
        PlayerId = playerId;
        LoginId = NormalizeLoginId(loginId);
        Role = role;
        CreatedAt = createdAt;
    }

    /// <summary>계정을 구별하는 변경 불가능한 식별자입니다.</summary>
    public Guid Id { get; private set; }

    /// <summary>이 계정이 조작할 Player의 식별자입니다.</summary>
    public Guid PlayerId { get; private set; }

    /// <summary>
    /// 정규화된 로그인 식별자입니다. 대소문자 혼동을 없애기 위해 소문자로 저장합니다.
    /// </summary>
    public string LoginId { get; private set; } = string.Empty;

    /// <summary>
    /// 비밀번호 원문이 아닌 PasswordHasher가 만든 검증용 해시입니다.
    /// 이 값에서 원래 비밀번호를 복원할 수 없어야 합니다.
    /// </summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>인가 정책이 사용할 계정 역할입니다.</summary>
    public AccountRole Role { get; private set; }

    /// <summary>계정이 생성된 UTC 기준 시각입니다.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// 응용 서비스가 만든 비밀번호 해시를 계정에 연결합니다.
    /// </summary>
    /// <param name="passwordHash">원문 비밀번호가 아닌 검증용 해시입니다.</param>
    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("비밀번호 해시는 비어 있을 수 없습니다.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    /// <summary>
    /// 입력한 로그인 식별자를 저장·조회에 동일하게 사용할 형태로 정규화하고 검증합니다.
    /// </summary>
    /// <param name="loginId">클라이언트가 입력한 로그인 식별자입니다.</param>
    /// <returns>앞뒤 공백을 제거하고 소문자로 통일한 로그인 식별자입니다.</returns>
    public static string NormalizeLoginId(string loginId)
    {
        ArgumentNullException.ThrowIfNull(loginId);

        var normalizedLoginId = loginId.Trim().ToLowerInvariant();

        if (normalizedLoginId.Length < 3 || normalizedLoginId.Length > MaxLoginIdLength)
        {
            throw new ArgumentException(
                $"로그인 식별자는 3자 이상 {MaxLoginIdLength}자 이하여야 합니다.",
                nameof(loginId));
        }

        if (normalizedLoginId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException(
                "로그인 식별자는 영문, 숫자, 밑줄만 사용할 수 있습니다.",
                nameof(loginId));
        }

        return normalizedLoginId;
    }
}
