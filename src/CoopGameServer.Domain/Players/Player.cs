namespace CoopGameServer.Domain.Players;

/// <summary>
/// 게임 서버에서 한 명의 플레이어를 나타내는 기본 도메인 엔티티입니다.
/// </summary>
/// <remarks>
/// Entity(엔티티)는 데이터베이스에 저장되어도 식별자(Id)로 같은 대상을 구분하는 객체입니다.
/// 재화와 인벤토리는 변경 특성과 책임이 다르므로 이후 별도 엔티티와 테이블로 분리합니다.
/// </remarks>
public sealed class Player
{
    /// <summary>
    /// 게임에서 허용하는 닉네임의 최대 글자 수입니다.
    /// 데이터베이스 열 길이와 입력 검증에서 같은 기준을 사용합니다.
    /// </summary>
    public const int MaxNicknameLength = 20;

    /// <summary>
    /// EF Core(Entity Framework Core)가 데이터베이스 행에서 객체를 만들 때 사용하는 생성자입니다.
    /// 일반 코드에서는 아래의 공개 생성자를 사용해 유효성 검사를 거친 Player를 만듭니다.
    /// </summary>
    private Player()
    {
    }

    /// <summary>
    /// 유효한 식별자와 닉네임으로 새 플레이어를 만듭니다.
    /// </summary>
    /// <param name="id">플레이어를 영구적으로 구별하는 Guid 식별자입니다.</param>
    /// <param name="nickname">화면에 표시할 플레이어 닉네임입니다.</param>
    /// <param name="createdAt">UTC 기준 생성 시각입니다.</param>
    /// <exception cref="ArgumentException">식별자 또는 닉네임이 유효하지 않을 때 발생합니다.</exception>
    public Player(Guid id, string nickname, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("플레이어 식별자는 비어 있을 수 없습니다.", nameof(id));
        }

        Id = id;
        Nickname = NormalizeNickname(nickname);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>
    /// 플레이어를 구별하는 변경 불가능한 식별자입니다.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// 화면에 표시할 닉네임입니다. 직접 변경하지 않고 Rename 메서드를 통해 수정합니다.
    /// </summary>
    public string Nickname { get; private set; } = string.Empty;

    /// <summary>
    /// 플레이어가 생성된 UTC 기준 시각입니다.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// 플레이어 정보가 마지막으로 수정된 UTC 기준 시각입니다.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 닉네임을 변경하고 마지막 수정 시각을 함께 갱신합니다.
    /// </summary>
    /// <param name="nickname">새 닉네임입니다.</param>
    /// <param name="updatedAt">UTC 기준 수정 시각입니다.</param>
    public void Rename(string nickname, DateTimeOffset updatedAt)
    {
        Nickname = NormalizeNickname(nickname);
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// 닉네임 입력을 저장 가능한 형태로 정리하고 게임 규칙을 검증합니다.
    /// </summary>
    private static string NormalizeNickname(string nickname)
    {
        ArgumentNullException.ThrowIfNull(nickname);

        // 실수로 입력한 앞뒤 공백은 저장하지 않습니다.
        var normalizedNickname = nickname.Trim();

        if (normalizedNickname.Length == 0)
        {
            throw new ArgumentException("닉네임은 공백만으로 구성할 수 없습니다.", nameof(nickname));
        }

        if (normalizedNickname.Length > MaxNicknameLength)
        {
            throw new ArgumentException(
                $"닉네임은 {MaxNicknameLength}자 이하여야 합니다.",
                nameof(nickname));
        }

        return normalizedNickname;
    }
}
