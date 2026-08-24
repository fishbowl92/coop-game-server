using System.Text;
using CoopGameServer.Api.Application.Authentication;
using CoopGameServer.Api.Application.GameRooms;
using CoopGameServer.Api.Application.Matchmaking;
using CoopGameServer.Api.Application.Parties;
using CoopGameServer.Api.Application.Rewards;
using CoopGameServer.Api.Authentication;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.Persistence;
using CoopGameServer.Persistence.Rewards;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// User Secrets(유저 시크릿, 개발 PC에만 비밀 설정을 저장하는 기능)에서
// PostgreSQL 연결 문자열을 읽습니다. 비밀번호가 코드나 Git에 들어가지 않게 하고,
// 값이 없으면 잘못된 DB 연결 상태로 서버가 실행되지 않도록 시작 단계에서 중단합니다.
var gameDbConnectionString = builder.Configuration.GetConnectionString("GameDb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:GameDb 설정이 없습니다. User Secrets에 PostgreSQL 연결 문자열을 설정하세요.");

// Factory는 호출마다 독립적인 GameDbContext를 만들 수 있어 HTTP 서비스와 Silo용 Writer가 함께 사용합니다.
// 기존 HTTP 서비스가 요청 범위 GameDbContext를 계속 주입받을 수 있도록 Scoped 등록도 연결합니다.
builder.Services.AddPooledDbContextFactory<GameDbContext>(options =>
    options.UseNpgsql(gameDbConnectionString));
builder.Services.AddScoped(serviceProvider =>
    serviceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>().CreateDbContext());

// JWT는 API가 "이 요청을 보낸 사람이 누구인가"를 확인하는 인증 수단입니다.
// SigningKey는 User Secrets에서만 읽으며, 토큰을 발급할 때와 검증할 때 같은 키를 사용합니다.
var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(jwtOptions);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

// Authorization(인가)은 인증된 사용자에게도 "어디까지 실행할 수 있는가"를 추가로 제한합니다.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        AuthorizationPolicies.AdministratorOnly,
        policy => policy.RequireRole(AccountRole.Administrator.ToString()));
});

// PasswordHasher는 비밀번호 원문을 저장하지 않고 salt를 포함한 검증용 해시만 만들고 비교합니다.
builder.Services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthenticationService>();

// Writer는 Factory와 TimeProvider만 보관하므로 Singleton으로 안전하게 재사용할 수 있습니다.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRewardWriter, PostgreSqlRewardWriter>();
// RewardService는 Controller가 PlayerGrain으로 전환될 때까지 HTTP DTO 변환만 담당합니다.
builder.Services.AddScoped<RewardService>();

// PartyService는 HTTP 요청과 PartyGrain 사이에서 서버 생성 partyId와
// 생성 요청 멱등성 재생을 조정하므로 HTTP 요청 단위로 등록합니다.
builder.Services.AddScoped<PartyService>();

// MatchmakingService는 인증된 사용자를 파티·대기열 Grain 흐름으로 안전하게 연결하고,
// GameRoomService는 매칭된 방의 최소 시작·완료 생명주기를 호출합니다.
builder.Services.AddScoped<MatchmakingService>();
builder.Services.AddScoped<GameRoomService>();

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

// 반드시 UseAuthorization보다 먼저 실행해야 JWT를 ClaimsPrincipal로 변환할 수 있습니다.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
