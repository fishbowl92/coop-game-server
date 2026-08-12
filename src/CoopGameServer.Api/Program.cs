using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// User Secrets(유저 시크릿, 개발 PC에만 비밀 설정을 저장하는 기능)에서
// PostgreSQL 연결 문자열을 읽습니다. 비밀번호가 코드나 Git에 들어가지 않게 하고,
// 값이 없으면 잘못된 DB 연결 상태로 서버가 실행되지 않도록 시작 단계에서 중단합니다.
var gameDbConnectionString = builder.Configuration.GetConnectionString("GameDb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:GameDb 설정이 없습니다. User Secrets에 PostgreSQL 연결 문자열을 설정하세요.");

// HTTP 요청마다 GameDbContext를 만들고 요청이 끝나면 정리하도록 등록합니다.
// Player·보상 데이터의 최종 원본은 계속 PostgreSQL에 저장합니다.
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(gameDbConnectionString));

// 보상 서비스도 HTTP 요청 단위의 GameDbContext를 공유하도록 Scoped 수명으로 등록합니다.
builder.Services.AddScoped<RewardService>();

// Orleans Client(클라이언트)는 Silo에 있는 Grain을 API 코드에서 호출하게 해 줍니다.
// 현재는 개발 PC의 단일 Silo에만 연결합니다. 운영 환경의 다중 Silo·클러스터 설정은
// 파티와 매칭 기능이 동작한 뒤 별도 단계에서 다룹니다.
builder.Host.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseLocalhostClustering();
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
