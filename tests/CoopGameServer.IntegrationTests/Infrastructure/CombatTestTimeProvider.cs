namespace CoopGameServer.IntegrationTests.Infrastructure;

/// <summary>직렬 테스트에서만 고정 시각을 주입합니다. 두 Silo와 재시작 뒤에도 같은 시계 인스턴스를 사용합니다.</summary>
public sealed class CombatTestTimeProvider : TimeProvider
{
    public static CombatTestTimeProvider Shared { get; } = new();
    private DateTimeOffset? _now;
    public override DateTimeOffset GetUtcNow() => _now ?? DateTimeOffset.UtcNow;
    public void Set(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan amount) => _now = GetUtcNow() + amount;
    public void Reset() => _now = null;
}
