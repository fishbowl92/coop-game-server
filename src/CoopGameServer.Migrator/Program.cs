using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;

const string ConnectionStringEnvironmentVariable = "ConnectionStrings__GameDb";

// 마이그레이션 실행기는 Compose 시작 순서에서 한 번 실행되는 단일 책임 프로세스입니다.
// API와 Silo가 동시에 스키마를 바꾸지 않게 하며, 실패하면 0이 아닌 종료 코드로 Silo 시작을 막습니다.
var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException($"{ConnectionStringEnvironmentVariable} 환경 변수가 없습니다.");
}

await using var dataSource = GameDbDataSourceFactory.Create(connectionString);
var options = new DbContextOptionsBuilder<GameDbContext>()
    .UseNpgsql(dataSource)
    .Options;
await using var dbContext = new GameDbContext(options);

var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();
Console.WriteLine($"Applying {pendingMigrations.Length} pending migration(s).");
await dbContext.Database.MigrateAsync();
Console.WriteLine("Database migrations completed.");
