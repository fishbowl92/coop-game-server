namespace CoopGameServer.Domain.GameRooms;

/// <summary>서버가 고정한 버전 1의 협동 전투 설정입니다. 클라이언트가 체력·피해량을 지정하지 않습니다.</summary>
public static class GameRoomCombatRules
{
    /// <summary>현재 새 방에 적용하는 전투 규칙 버전입니다.</summary>
    public const int CurrentVersion = 1;
    /// <summary>한 경기의 전체 웨이브 수입니다.</summary>
    public const int WaveCount = 3;
    /// <summary>참가자의 초기 최대·현재 체력입니다.</summary>
    public const int InitialPlayerHealth = 100;

    /// <summary>방에 고정된 버전의 공격 수치를 반환합니다. 외부 요청의 피해량은 받지 않습니다.</summary>
    /// <param name="ruleVersion">방 생성 시 저장한 전투 규칙 버전입니다.</param>
    public static CombatRuleSet GetCombatRuleSet(int ruleVersion)
    {
        if (ruleVersion != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleVersion), ruleVersion, "지원하지 않는 전투 규칙 버전입니다.");
        }

        return new CombatRuleSet(20, TimeSpan.FromSeconds(1), 50, TimeSpan.FromSeconds(5));
    }

    /// <summary>저장된 사용 가능 시각과 서버 시각만 비교합니다. 현재 시각을 직접 읽거나 상태를 변경하지 않습니다.</summary>
    /// <param name="serverNow">호출자가 명령 처리 시작 시 한 번 읽은 서버 시각입니다.</param>
    /// <param name="readyAt">다음 사용 가능 시각입니다. null이면 아직 사용한 적이 없어 즉시 허용합니다.</param>
    public static bool IsCooldownReady(DateTimeOffset serverNow, DateTimeOffset? readyAt)
        => readyAt is null || serverNow >= readyAt.Value;

    /// <summary>고정 참가자 순서에서 살아 있는 참가자만 세어 이번 반격 대상의 원래 인덱스를 반환합니다.</summary>
    /// <remarks>연결 이탈자는 체력이 남아 있으면 포함합니다. 호출자가 반환 대상을 공격한 뒤 순번을 증가시킵니다.</remarks>
    /// <param name="playerHealth">player_order 순서의 네 참가자 체력입니다. 입력 배열은 수정하지 않습니다.</param>
    /// <param name="enemyAttackSequence">이미 적용한 반격 횟수입니다. 상태 버전과 독립적인 값입니다.</param>
    public static int SelectCounterattackTarget(IReadOnlyList<int> playerHealth, long enemyAttackSequence)
    {
        ArgumentNullException.ThrowIfNull(playerHealth);
        ArgumentOutOfRangeException.ThrowIfNegative(enemyAttackSequence);
        if (playerHealth.Count != 4)
        {
            throw new ArgumentException("반격 대상은 고정 순서의 네 참가자여야 합니다.", nameof(playerHealth));
        }

        var activeCount = 0;
        foreach (var health in playerHealth)
        {
            if (health < 0 || health > InitialPlayerHealth)
            {
                throw new ArgumentOutOfRangeException(nameof(playerHealth), "참가자 체력 범위가 올바르지 않습니다.");
            }

            if (health > 0)
            {
                activeCount++;
            }
        }

        if (activeCount == 0)
        {
            throw new InvalidOperationException("전원이 전투 불능이면 반격 대신 패배를 판정해야 합니다.");
        }

        // 증가 후 오버플로가 나는 입력은 실제 반격을 적용하기 전에 거부합니다.
        if (enemyAttackSequence == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(enemyAttackSequence), "반격 순번을 더 증가시킬 수 없습니다.");
        }

        var targetOrdinal = enemyAttackSequence % activeCount;
        for (var index = 0; index < playerHealth.Count; index++)
        {
            if (playerHealth[index] > 0 && targetOrdinal-- == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("반격 대상을 선택하지 못했습니다.");
    }

    /// <summary>방에 고정된 규칙 버전과 웨이브 번호에 해당하는 불변 설정을 반환합니다.</summary>
    /// <param name="ruleVersion">방 생성 시 저장한 버전입니다. 과거 기록용 0과 미지원 버전은 실행하지 않습니다.</param>
    /// <param name="waveNumber">1부터 3까지의 웨이브 번호입니다.</param>
    public static WaveDefinition GetWave(int ruleVersion, int waveNumber)
    {
        if (ruleVersion != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleVersion), ruleVersion, "지원하지 않는 전투 규칙 버전입니다.");
        }

        return waveNumber switch
        {
            1 => new WaveDefinition(1, 100, 5),
            2 => new WaveDefinition(2, 180, 10),
            3 => new WaveDefinition(3, 300, 15),
            _ => throw new ArgumentOutOfRangeException(nameof(waveNumber), waveNumber, "웨이브 번호는 1~3이어야 합니다."),
        };
    }
}

/// <summary>한 웨이브의 서버 설정입니다. 현재 체력처럼 변경되는 진행 상태와 분리합니다.</summary>
/// <param name="WaveNumber">웨이브 번호입니다.</param>
/// <param name="EnemyMaxHealth">적 최대 체력입니다.</param>
/// <param name="EnemyAttackPower">적 반격의 기본 피해량입니다. 반격 처리는 후속 공격 단계에서 사용합니다.</param>
public sealed record WaveDefinition(int WaveNumber, int EnemyMaxHealth, int EnemyAttackPower);

/// <summary>서버의 버전별 공격 정책입니다. 실제 참가자의 쿨다운 시각과 구분합니다.</summary>
/// <param name="BasicAttackDamage">일반 공격 피해량입니다.</param>
/// <param name="BasicAttackCooldown">일반 공격 성공 후 재사용 대기시간입니다.</param>
/// <param name="SkillDamage">스킬 피해량입니다.</param>
/// <param name="SkillCooldown">스킬 성공 후 재사용 대기시간입니다.</param>
public sealed record CombatRuleSet(int BasicAttackDamage, TimeSpan BasicAttackCooldown, int SkillDamage, TimeSpan SkillCooldown);
