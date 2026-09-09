using CoopGameServer.Domain.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

/// <summary>DB 없이 고정된 전투 버전·웨이브 설정의 경계를 검증합니다.</summary>
public sealed class GameRoomCombatRulesTests
{
    [Theory]
    [InlineData(1, 100, 5)]
    [InlineData(2, 180, 10)]
    [InlineData(3, 300, 15)]
    public void GetWaveReturnsVersionOneDefinition(int number, int health, int attack)
    {
        var wave = GameRoomCombatRules.GetWave(1, number);
        Assert.Equal(number, wave.WaveNumber);
        Assert.Equal(health, wave.EnemyMaxHealth);
        Assert.Equal(attack, wave.EnemyAttackPower);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void GetWaveRejectsUnsupportedVersion(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>("ruleVersion", () => GameRoomCombatRules.GetWave(version, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void GetWaveRejectsOutOfRangeWave(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>("waveNumber", () => GameRoomCombatRules.GetWave(1, number));
    }
}
