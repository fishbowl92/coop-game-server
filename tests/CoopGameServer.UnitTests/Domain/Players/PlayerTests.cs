using CoopGameServer.Api.Domain.Players;

namespace CoopGameServer.UnitTests.Domain.Players;

/// <summary>
/// Player 엔티티가 게임 규칙에 맞는 상태만 가질 수 있는지 검증합니다.
/// </summary>
public sealed class PlayerTests
{
    [Fact]
    public void ConstructorCreatesPlayerWithTrimmedNicknameAndMatchingTimestamps()
    {
        // 테스트에서 예측 가능한 결과를 만들기 위해 고정된 식별자와 시각을 사용합니다.
        var id = Guid.Parse("7c3fc9a4-5478-4e26-bda6-6eb6a4d5a9b2");
        var createdAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

        var player = new Player(id, "  Minwoo  ", createdAt);

        // 생성 시 앞뒤 공백은 제거되고, 생성·수정 시각은 같은 값으로 초기화되어야 합니다.
        Assert.Equal(id, player.Id);
        Assert.Equal("Minwoo", player.Nickname);
        Assert.Equal(createdAt, player.CreatedAt);
        Assert.Equal(createdAt, player.UpdatedAt);
    }

    [Fact]
    public void RenameChangesNicknameAndUpdatesTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(5);
        var player = new Player(Guid.NewGuid(), "Minwoo", createdAt);

        player.Rename("CoopMaster", updatedAt);

        // 닉네임 변경은 CreatedAt을 바꾸지 않고 UpdatedAt만 갱신해야 합니다.
        Assert.Equal("CoopMaster", player.Nickname);
        Assert.Equal(createdAt, player.CreatedAt);
        Assert.Equal(updatedAt, player.UpdatedAt);
    }

    [Fact]
    public void ConstructorRejectsWhitespaceOnlyNickname()
    {
        // 공백만 있는 닉네임은 플레이어 이름으로 사용할 수 없어야 합니다.
        Assert.Throws<ArgumentException>(
            () => new Player(Guid.NewGuid(), "   ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ConstructorRejectsNicknameLongerThanMaximumLength()
    {
        var nicknameLongerThanLimit = new string('a', Player.MaxNicknameLength + 1);

        // 데이터베이스 열 길이와 같은 최대 길이 규칙을 엔티티 생성 단계에서 먼저 막습니다.
        Assert.Throws<ArgumentException>(
            () => new Player(Guid.NewGuid(), nicknameLongerThanLimit, DateTimeOffset.UtcNow));
    }
}
