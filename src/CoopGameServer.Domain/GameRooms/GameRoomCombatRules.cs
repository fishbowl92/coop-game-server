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
