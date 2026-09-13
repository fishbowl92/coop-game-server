using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.Grains.GameRooms;

namespace CoopGameServer.UnitTests.Domain.GameRooms;

/// <summary>서버와 DB 없이 후보 상태의 전투·최초 결과 재생을 검증합니다.</summary>
public sealed class GameRoomCombatTransitionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void AttackChangesOnlyCandidateAndReplayPreservesFirstResult()
    {
        var original = Create();
        var state = original.Clone();
        var command = Command(state);
        var result = state.ExecuteCombat(command, Now);
        Assert.Equal(GameRoomCombatError.None, result.Error);
        Assert.Equal(80, result.Room!.EnemyCurrentHealth);
        Assert.Equal(95, result.Room.Players![0].CurrentHealth);
        Assert.Equal(1, result.Room.EnemyAttackSequence);
        Assert.Equal(3, result.Room.StateVersion);
        Assert.Equal(1, result.Room.Players[0].LastAcceptedCommandSequence);
        Assert.Equal(Now.AddSeconds(1), result.Room.Players[0].BasicAttackReadyAt);
        Assert.Null(result.Room.Players[0].SkillReadyAt);
        Assert.Equal(100, original.Get()!.EnemyCurrentHealth);

        // 이전 응답을 호출자가 변경해도 내부 상태·최초 기록은 달라지지 않아야 합니다.
        result.Room.Players[0] = result.Room.Players[0] with { CurrentHealth = 0 };
        var restored = state.Clone();
        var replay = restored.ExecuteCombat(command, Now.AddHours(1));
        Assert.True(replay.IsReplay);
        Assert.Equal(95, replay.Room!.Players![0].CurrentHealth);
        Assert.Equal(3, restored.Get()!.StateVersion);
    }

    [Fact]
    public void CooldownRejectionIsStableButNewKeyCanRetrySameSequence()
    {
        var state = Create();
        state.ExecuteCombat(Command(state), Now);
        var second = Command(state) with { Sequence = 2 };
        var rejected = state.ExecuteCombat(second, Now);
        Assert.Equal(GameRoomCombatError.CooldownActive, rejected.Error);
        Assert.Equal(Now.AddSeconds(1), rejected.RetryAt);
        Assert.Equal(1, state.Get()!.Players![0].LastAcceptedCommandSequence);
        var replay = state.Clone().ExecuteCombat(second, Now.AddSeconds(10));
        Assert.True(replay.IsReplay);
        Assert.Equal(GameRoomCombatError.CooldownActive, replay.Error);
        Assert.Equal(GameRoomCombatError.None, state.ExecuteCombat(second with { RequestId = Guid.NewGuid() }, Now.AddSeconds(1)).Error);
    }

    [Fact]
    public void KillAdvancesWaveWithoutCounterattackOrHealing()
    {
        var state = Create(enemyHealth: 20);
        var result = state.ExecuteCombat(Command(state), Now);
        Assert.Equal(2, result.Room!.CurrentWave);
        Assert.Equal(180, result.Room.EnemyCurrentHealth);
        Assert.Equal(0, result.Room.EnemyAttackSequence);
        Assert.Equal(100, result.Room.Players![0].CurrentHealth);
        Assert.Equal(3, result.Room.StateVersion);
    }

    [Fact]
    public void FinalKillWinsAndTerminalReplayStillWorks()
    {
        var state = Create(wave: 3, enemyHealth: 10);
        var command = Command(state) with { Kind = CombatActionKind.UseSkill };
        var result = state.ExecuteCombat(command, Now);
        Assert.Equal(GameOutcome.Victory, result.Room!.Outcome);
        Assert.Equal(GameRoomLifecycle.Completed, result.Room.Lifecycle);
        Assert.Equal(Now, result.Room.CompletedAt);
        Assert.Equal(0, result.Room.EnemyCurrentHealth);
        Assert.Equal(Now.AddSeconds(5), result.Room.Players![0].SkillReadyAt);
        Assert.True(state.ExecuteCombat(command, Now).IsReplay);
        Assert.Equal(GameRoomCombatError.RoomCompleted, state.ExecuteCombat(Command(state), Now).Error);
    }

    [Fact]
    public void CounterattackCanDefeatLastSurvivor()
    {
        var state = Create();
        var room = state.Get()!;
        room = room with { Players = room.Players!.Select((p, i) => p with { CurrentHealth = i == 0 ? 1 : 0, CombatStatus = i == 0 ? PlayerCombatStatus.Active : PlayerCombatStatus.Incapacitated }).ToArray() };
        state = GameRoomState.Restore(room, []);
        var result = state.ExecuteCombat(Command(state), Now);
        Assert.Equal(GameOutcome.Defeat, result.Room!.Outcome);
        Assert.All(result.Room.Players!, p => Assert.Equal(0, p.CurrentHealth));
        Assert.Equal(Now, result.Room.CompletedAt);
    }

    [Fact]
    public void ConflictAndUnauthorizedReplayDoNotChangeState()
    {
        var state = Create();
        var command = Command(state);
        state.ExecuteCombat(command, Now);
        Assert.Equal(GameRoomCombatError.RequestIdConflict, state.ExecuteCombat(command with { Kind = CombatActionKind.UseSkill }, Now).Error);
        var denied = state.ExecuteCombat(command with { PlayerId = Guid.NewGuid() }, Now);
        Assert.Equal(GameRoomCombatError.PlayerNotInRoom, denied.Error);
        Assert.Null(denied.Room);
        Assert.Equal(3, state.Get()!.StateVersion);
    }

    [Theory]
    [InlineData(0, (int)GameRoomCombatError.InvalidCommand)]
    [InlineData(2, (int)GameRoomCombatError.SequenceGap)]
    public void InvalidSequenceDoesNotDamage(long sequence, int expected)
    {
        var state = Create();
        Assert.Equal((GameRoomCombatError)expected, state.ExecuteCombat(Command(state) with { Sequence = sequence }, Now).Error);
        Assert.Equal(100, state.Get()!.EnemyCurrentHealth);
    }

    [Fact]
    public void PastSequenceAndFutureVersionAreRejected()
    {
        var state = Create();
        state.ExecuteCombat(Command(state), Now);
        Assert.Equal(GameRoomCombatError.SequenceAlreadyPassed, state.ExecuteCombat(Command(state), Now.AddSeconds(2)).Error);
        Assert.Equal(GameRoomCombatError.InvalidKnownStateVersion, state.ExecuteCombat(Command(state) with { Sequence = 2, KnownStateVersion = 100 }, Now).Error);
        Assert.Equal(GameRoomCombatError.None, state.ExecuteCombat(Command(state) with { Sequence = 2, KnownStateVersion = 1 }, Now.AddSeconds(1)).Error);
    }

    [Fact]
    public void ExhaustedCountersAndTimeAreRejectedBeforeMutation()
    {
        var initial = Create().Get()!;
        var version = GameRoomState.Restore(initial with { StateVersion = long.MaxValue }, []);
        Assert.Equal(GameRoomCombatError.StateVersionExhausted, version.ExecuteCombat(Command(version), Now).Error);
        var enemy = GameRoomState.Restore(initial with { EnemyAttackSequence = long.MaxValue }, []);
        Assert.Equal(GameRoomCombatError.EnemySequenceExhausted, enemy.ExecuteCombat(Command(enemy), Now).Error);
        Assert.Equal(100, enemy.Get()!.EnemyCurrentHealth);
        var time = GameRoomState.Restore(initial, []);
        Assert.Equal(GameRoomCombatError.TimeRangeExceeded, time.ExecuteCombat(Command(time), DateTimeOffset.MaxValue).Error);
    }

    private static GameRoomCombatCommand Command(GameRoomState state)
        => new(Guid.NewGuid(), state.Get()!.PlayerIds[0], 1, CombatActionKind.BasicAttack);

    private static GameRoomState Create(int wave = 1, int enemyHealth = 100)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var room = new GameRoomSnapshot(Guid.NewGuid(), "combat-test", GameRoomLifecycle.InGame, [], ids,
            Now, Now, null, GameOutcome.None, 1, 1,
            ids.Select((id, index) => new PlayerCombatSnapshot(id, index, 100, 100, PlayerCombatStatus.Active, 0, null, null)).ToArray(),
            wave, 3, wave == 3 ? 300 : 100, enemyHealth, 2, 0);
        return GameRoomState.Restore(room, []);
    }
}
