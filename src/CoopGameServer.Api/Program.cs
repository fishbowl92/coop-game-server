using CoopGameServer.Api.Data;
using CoopGameServer.Api.Application.Rewards;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// 연결 문자열은 User Secrets(로컬 개발용 비밀 저장소)에서 다음 단계에 설정합니다.
// 소스 코드나 Git에 비밀번호를 넣지 않기 위해, 값이 없으면 서버 시작을 중단합니다.
var gameDbConnectionString = builder.Configuration.GetConnectionString("GameDb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:GameDb 설정이 없습니다. User Secrets에 PostgreSQL 연결 문자열을 설정하세요.");

// 요청마다 필요한 GameDbContext를 만들고, 작업이 끝나면 안전하게 정리하도록 등록합니다.
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(gameDbConnectionString));

// 보상 서비스도 HTTP 요청마다 독립 인스턴스를 사용하여 해당 요청의 GameDbContext와 같은 작업 범위를 공유합니다.
builder.Services.AddScoped<RewardService>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
