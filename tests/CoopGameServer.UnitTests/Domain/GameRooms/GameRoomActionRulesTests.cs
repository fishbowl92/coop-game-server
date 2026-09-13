using CoopGameServer.Domain.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

/// <summary>외부 통신 없이 공격 수치·시간 경계·반격 순서의 결정성을 검증합니다.</summary>
public sealed class GameRoomActionRulesTests
{
    [Fact]
    public void VersionOneUsesServerOwnedDamageAndCooldown()
    {
        var rules = GameRoomCombatRules.GetCombatRuleSet(1);
        Assert.Equal(20, rules.BasicAttackDamage);
        Assert.Equal(TimeSpan.FromSeconds(1), rules.BasicAttackCooldown);
        Assert.Equal(50, rules.SkillDamage);
        Assert.Equal(TimeSpan.FromSeconds(5), rules.SkillCooldown);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedCombatVersionIsRejected(int version)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GameRoomCombatRules.GetCombatRuleSet(version));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void CooldownAllowsExactDeadlineAndLater(long ticksAfterDeadline, bool expected)
    {
        var deadline = DateTimeOffset.UnixEpoch.AddSeconds(5);
        Assert.Equal(expected, GameRoomCombatRules.IsCooldownReady(deadline.AddTicks(ticksAfterDeadline), deadline));
    }

    [Fact]
    public void UnusedActionIsReadyAndOffsetsCompareAsInstants()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.True(GameRoomCombatRules.IsCooldownReady(now, null));
        Assert.True(GameRoomCombatRules.IsCooldownReady(now, now.ToOffset(TimeSpan.FromHours(9))));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(long.MaxValue - 1, 0)]
    public void CounterattackSkipsDefeatedPlayersWithoutChangingOrder(long sequence, int expectedIndex)
    {
        int[] health = [100, 0, 20, 1];
        Assert.Equal(expectedIndex, GameRoomCombatRules.SelectCounterattackTarget(health, sequence));
        int[] expectedHealth = [100, 0, 20, 1];
        Assert.Equal(expectedHealth, health);
    }

    [Fact]
    public void OnlySurvivorAlwaysReceivesCounterattack()
    {
        int[] health = [0, 0, 1, 0];
        Assert.Equal(2, GameRoomCombatRules.SelectCounterattackTarget(health, 99));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void InvalidOrExhaustedSequenceIsRejected(long sequence)
    {
        int[] health = [100, 100, 100, 100];
        Assert.Throws<ArgumentOutOfRangeException>(() => GameRoomCombatRules.SelectCounterattackTarget(health, sequence));
    }

    [Fact]
    public void AllDefeatedIsNotAValidCounterattack()
    {
        int[] health = [0, 0, 0, 0];
        Assert.Throws<InvalidOperationException>(() => GameRoomCombatRules.SelectCounterattackTarget(health, 0));
    }

    [Fact]
    public void InvalidParticipantsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => GameRoomCombatRules.SelectCounterattackTarget(null!, 0));
        int[] incomplete = [100];
        int[] negative = [-1, 100, 100, 100];
        int[] excessive = [101, 100, 100, 100];
        Assert.Throws<ArgumentException>(() => GameRoomCombatRules.SelectCounterattackTarget(incomplete, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameRoomCombatRules.SelectCounterattackTarget(negative, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameRoomCombatRules.SelectCounterattackTarget(excessive, 0));
    }
}
