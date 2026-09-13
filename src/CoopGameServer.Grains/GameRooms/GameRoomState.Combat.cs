using CoopGameServer.Domain.GameRooms;
using CoopGameServer.GrainContracts.GameRooms;

namespace CoopGameServer.Grains.GameRooms;

/// <summary>전투 후보 상태 계산입니다. 호출하는 Grain이 현재 연결을 검증하고 DB 커밋 후 후보를 채택합니다.</summary>
internal sealed partial class GameRoomState
{
    /// <summary>원본 대신 Clone한 후보에 호출합니다. 거부는 상태를 바꾸지 않고 최초 결과만 기억합니다.</summary>
    /// <param name="command">서버가 인증한 참가자와 요청 키·순번·공격 종류입니다. 피해량은 받지 않습니다.</param>
    /// <param name="now">호출자가 한 번 읽은 서버 시각입니다. 재생에는 새 시각을 적용하지 않습니다.</param>
    internal GameRoomCombatResult ExecuteCombat(GameRoomCombatCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequestId == Guid.Empty || command.PlayerId == Guid.Empty || command.Sequence <= 0
            || !Enum.IsDefined(command.Kind))
        {
            return new(GameRoomCombatError.InvalidCommand, false, null, null);
        }

        if (_room is null)
        {
            return new(GameRoomCombatError.RoomNotCreated, false, null, null);
        }

        // 다른 참가자가 알아낸 요청 키로 최초 결과를 조회하지 못하도록 재생 전에 소속을 확인합니다.
        if (!_room.PlayerIds.Contains(command.PlayerId))
        {
            return new(GameRoomCombatError.PlayerNotInRoom, false, null, null);
        }

        if (_requests.TryGetValue(command.RequestId, out var stored))
        {
            return stored.CommandKind == GameRoomCommandKind.Combat && stored.CombatCommand == command
                ? new(stored.CombatError, true, CloneSnapshot(stored.Result.Room), stored.RetryAt)
                : new(GameRoomCombatError.RequestIdConflict, false, Get(), null);
        }

        GameRoomCombatResult Reject(GameRoomCombatError error, DateTimeOffset? retryAt = null)
            => RememberCombat(command, now, error, retryAt);

        if (_room.Lifecycle != GameRoomLifecycle.InGame)
        {
            return Reject(_room.Lifecycle == GameRoomLifecycle.Completed
                ? GameRoomCombatError.RoomCompleted : GameRoomCombatError.RoomNotInGame);
        }

        if (_room.CombatRuleVersion != GameRoomCombatRules.CurrentVersion || _room.Players is not { Length: 4 })
        {
            return Reject(GameRoomCombatError.UnsupportedCombatState);
        }

        var players = _room.Players.OrderBy(player => player.PlayerOrder).ToArray();
        var actorIndex = Array.FindIndex(players, player => player.PlayerId == command.PlayerId);
        if (actorIndex < 0)
        {
            return Reject(GameRoomCombatError.UnsupportedCombatState);
        }

        var actor = players[actorIndex];
        if (actor.CombatStatus != PlayerCombatStatus.Active || actor.CurrentHealth <= 0)
        {
            return Reject(GameRoomCombatError.PlayerIncapacitated);
        }

        if (actor.LastAcceptedCommandSequence == long.MaxValue)
        {
            return Reject(GameRoomCombatError.SequenceExhausted);
        }

        if (command.Sequence != actor.LastAcceptedCommandSequence + 1)
        {
            return Reject(command.Sequence <= actor.LastAcceptedCommandSequence
                ? GameRoomCombatError.SequenceAlreadyPassed : GameRoomCombatError.SequenceGap);
        }

        if (command.KnownStateVersion is { } known && (known < 0 || known > _room.StateVersion))
        {
            return Reject(GameRoomCombatError.InvalidKnownStateVersion);
        }

        var readyAt = command.Kind == CombatActionKind.BasicAttack ? actor.BasicAttackReadyAt : actor.SkillReadyAt;
        if (!GameRoomCombatRules.IsCooldownReady(now, readyAt))
        {
            return Reject(GameRoomCombatError.CooldownActive, readyAt);
        }

        if (_room.StateVersion == long.MaxValue)
        {
            return Reject(GameRoomCombatError.StateVersionExhausted);
        }

        var rules = GameRoomCombatRules.GetCombatRuleSet(_room.CombatRuleVersion);
        var damage = command.Kind == CombatActionKind.BasicAttack ? rules.BasicAttackDamage : rules.SkillDamage;
        var cooldown = command.Kind == CombatActionKind.BasicAttack ? rules.BasicAttackCooldown : rules.SkillCooldown;
        if (now > DateTimeOffset.MaxValue - cooldown)
        {
            return Reject(GameRoomCombatError.TimeRangeExceeded);
        }

        var enemyHealth = Math.Max(0, _room.EnemyCurrentHealth - damage);
        // 순번 상한이어도 적을 처치하여 반격이 없으면 공격 자체는 허용합니다.
        if (enemyHealth > 0 && _room.EnemyAttackSequence == long.MaxValue)
        {
            return Reject(GameRoomCombatError.EnemySequenceExhausted);
        }

        players[actorIndex] = actor with
        {
            LastAcceptedCommandSequence = command.Sequence,
            BasicAttackReadyAt = command.Kind == CombatActionKind.BasicAttack ? now + cooldown : actor.BasicAttackReadyAt,
            SkillReadyAt = command.Kind == CombatActionKind.UseSkill ? now + cooldown : actor.SkillReadyAt,
        };

        var candidate = _room with
        {
            Players = players,
            EnemyCurrentHealth = enemyHealth,
            StateVersion = _room.StateVersion + 1,
        };

        if (enemyHealth == 0)
        {
            if (candidate.CurrentWave == candidate.MaxWaves)
            {
                candidate = candidate with { Lifecycle = GameRoomLifecycle.Completed, Outcome = GameOutcome.Victory, CompletedAt = now };
            }
            else
            {
                var wave = GameRoomCombatRules.GetWave(candidate.CombatRuleVersion, candidate.CurrentWave + 1);
                candidate = candidate with { CurrentWave = wave.WaveNumber, EnemyMaxHealth = wave.EnemyMaxHealth, EnemyCurrentHealth = wave.EnemyMaxHealth };
            }
        }
        else
        {
            var targetIndex = GameRoomCombatRules.SelectCounterattackTarget(
                players.Select(player => player.CurrentHealth).ToArray(), candidate.EnemyAttackSequence);
            var target = players[targetIndex];
            var wave = GameRoomCombatRules.GetWave(candidate.CombatRuleVersion, candidate.CurrentWave);
            var health = Math.Max(0, target.CurrentHealth - wave.EnemyAttackPower);
            players[targetIndex] = target with
            {
                CurrentHealth = health,
                CombatStatus = health == 0 ? PlayerCombatStatus.Incapacitated : PlayerCombatStatus.Active,
            };
            candidate = candidate with { EnemyAttackSequence = candidate.EnemyAttackSequence + 1 };
            if (players.All(player => player.CurrentHealth == 0))
            {
                candidate = candidate with { Lifecycle = GameRoomLifecycle.Completed, Outcome = GameOutcome.Defeat, CompletedAt = now };
            }
        }

        _room = candidate;
        if (_room.Lifecycle == GameRoomLifecycle.Completed)
        {
            // 마지막 공격의 응답에도 종료된 연결 상태가 즉시 반영되어야 합니다.
            CloseConnections();
        }
        return RememberCombat(command, now, GameRoomCombatError.None, null);
    }

    /// <summary>거부도 최초 결과로 기억해 시간 경과로 같은 요청의 결과가 바뀌지 않도록 합니다.</summary>
    internal GameRoomCombatResult RememberCombat(GameRoomCombatCommand command, DateTimeOffset now, GameRoomCombatError error, DateTimeOffset? retryAt)
    {
        var snapshot = Get();
        _requests.Add(command.RequestId, new GameRoomStoredRequest(
            command.RequestId, GameRoomCommandKind.Combat, null, null,
            new GameRoomCommandResult(false, GameRoomCommandError.None, CloneSnapshot(snapshot), null, null),
            now, command, error, retryAt));
        return new(error, false, snapshot, retryAt);
    }
}
