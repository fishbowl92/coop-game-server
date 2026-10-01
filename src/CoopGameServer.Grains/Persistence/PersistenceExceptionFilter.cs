using CoopGameServer.GrainContracts.Persistence;
using Npgsql;

namespace CoopGameServer.Grains.Persistence;

/// <summary>모든 Grain 경계에서 Npgsql 예외를 Orleans로 전송 가능한 안전한 오류로 변환합니다.</summary>
public sealed class PersistenceExceptionFilter : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        try
        {
            await context.Invoke();
        }
        catch (Exception exception) when (FindDatabaseException(exception) is { } databaseException)
        {
            throw Convert(databaseException);
        }
    }

    // EF의 실행 전략은 일시 오류를 InvalidOperationException → DbUpdateException으로 감쌀 수 있습니다.
    // 첫 InnerException만 확인하면 바로 그 재시도 대상 오류가 원격 직렬화 단계에서 다시 실패합니다.
    private static NpgsqlException? FindDatabaseException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is NpgsqlException databaseException) return databaseException;
        return null;
    }

    // 원래 예외를 InnerException으로 넣으면 직렬화할 수 없는 드라이버 객체가 다시 전달됩니다.
    private static GrainPersistenceException Convert(NpgsqlException exception) =>
        new(exception is PostgresException postgres ? postgres.SqlState : "ConnectionFailure", exception.IsTransient);
}
