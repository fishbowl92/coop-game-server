using CoopGameServer.Api.Authentication;
using CoopGameServer.Contracts.Authentication;
using CoopGameServer.Domain.Accounts;
using CoopGameServer.Domain.Players;
using CoopGameServer.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoopGameServer.Api.Application.Authentication;

/// <summary>
/// 회원 가입·로그인·비밀번호 해시 검증을 처리하는 응용 서비스입니다.
/// </summary>
/// <remarks>
/// Controller는 HTTP 상태 코드만 처리하고, 이 서비스는 Player와 Account를 함께 저장하는 업무 흐름을 책임집니다.
/// </remarks>
public sealed class AuthenticationService(
    GameDbContext gameDbContext,
    IPasswordHasher<Account> passwordHasher,
    JwtTokenService jwtTokenService)
{
    private const int MinPasswordLength = 8;

    private readonly GameDbContext _gameDbContext = gameDbContext;
    private readonly IPasswordHasher<Account> _passwordHasher = passwordHasher;
    private readonly JwtTokenService _jwtTokenService = jwtTokenService;

    /// <summary>
    /// 새 Player와 일반 Player 역할의 Account를 같은 저장 작업으로 생성하고 곧바로 로그인 토큰을 발급합니다.
    /// </summary>
    /// <param name="request">로그인 식별자·비밀번호·게임 닉네임을 담은 요청입니다.</param>
    /// <param name="cancellationToken">요청 연결이 종료되면 DB 작업을 취소하는 토큰입니다.</param>
    /// <returns>새 Player 식별자와 접근 토큰입니다.</returns>
    /// <exception cref="ArgumentException">입력 규칙이 맞지 않을 때 발생합니다.</exception>
    /// <exception cref="DuplicateAccountException">로그인 식별자 또는 닉네임이 이미 있을 때 발생합니다.</exception>
    public async Task<AuthenticationResult> RegisterAsync(
        RegisterAccountRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePassword(request.Password);

        var now = DateTimeOffset.UtcNow;
        var player = new Player(Guid.NewGuid(), request.Nickname, now);
        var account = new Account(
            Guid.NewGuid(),
            player.Id,
            request.LoginId,
            AccountRole.Player,
            now);

        // PasswordHasher는 salt(솔트, 같은 비밀번호도 다른 해시가 되게 하는 임의 값)를 포함한 결과를 만듭니다.
        // 원문 request.Password는 이 호출 뒤 어떤 엔티티나 DB 열에도 넣지 않습니다.
        account.SetPasswordHash(_passwordHasher.HashPassword(account, request.Password));

        _gameDbContext.Players.Add(player);
        _gameDbContext.Accounts.Add(account);

        try
        {
            // EF Core는 이 SaveChangesAsync 안에서 Player INSERT와 Account INSERT를 하나의 트랜잭션으로 처리합니다.
            // 둘 중 하나가 실패하면 회원 가입 중간 상태가 저장되지 않습니다.
            await _gameDbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new DuplicateAccountException(
                HasConstraint(exception, "IX_accounts_login_id") ? "loginId" : "nickname");
        }

        return CreateResult(account);
    }

    /// <summary>
    /// 저장된 비밀번호 해시와 입력 원문을 비교해 성공할 때만 새 접근 토큰을 발급합니다.
    /// </summary>
    /// <param name="request">로그인 식별자와 비밀번호 원문입니다.</param>
    /// <param name="cancellationToken">요청 연결이 종료되면 DB 작업을 취소하는 토큰입니다.</param>
    /// <returns>인증에 성공하면 결과를, 식별자 또는 비밀번호가 틀리면 null을 반환합니다.</returns>
    public async Task<AuthenticationResult?> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 로그인 식별자 규칙이 맞지 않으면 존재 여부를 알려 주지 않고 동일한 로그인 실패로 처리합니다.
        string normalizedLoginId;
        try
        {
            normalizedLoginId = Account.NormalizeLoginId(request.LoginId);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var account = await _gameDbContext.Accounts
            .SingleOrDefaultAsync(entity => entity.LoginId == normalizedLoginId, cancellationToken);

        if (account is null || string.IsNullOrEmpty(request.Password))
        {
            return null;
        }

        var verificationResult = _passwordHasher.VerifyHashedPassword(
            account,
            account.PasswordHash,
            request.Password);

        if (verificationResult is PasswordVerificationResult.Failed)
        {
            return null;
        }

        // 라이브러리의 해시 방식이 향상됐을 때, 성공 로그인 시점에 새 방식으로 자연스럽게 교체합니다.
        if (verificationResult is PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.SetPasswordHash(_passwordHasher.HashPassword(account, request.Password));
            await _gameDbContext.SaveChangesAsync(cancellationToken);
        }

        return CreateResult(account);
    }

    /// <summary>비밀번호 최소 길이와 공백 전용 입력을 회원 가입 시점에 검증합니다.</summary>
    private static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length < MinPasswordLength || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException(
                $"비밀번호는 공백이 아닌 문자를 포함해 {MinPasswordLength}자 이상이어야 합니다.",
                nameof(password));
        }
    }

    /// <summary>계정·닉네임 유일성 제약 위반을 PostgreSQL 오류 코드로 판별합니다.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };
    }

    /// <summary>유일성 제약 오류가 어떤 인덱스에서 왔는지 확인합니다.</summary>
    private static bool HasConstraint(DbUpdateException exception, string constraintName)
    {
        return exception.InnerException is PostgresException
        {
            ConstraintName: var actualConstraintName,
        } && string.Equals(actualConstraintName, constraintName, StringComparison.Ordinal);
    }

    /// <summary>저장된 계정과 새 JWT를 외부 응답으로 변환합니다.</summary>
    private AuthenticationResult CreateResult(Account account)
    {
        var token = _jwtTokenService.CreateAccessToken(account);
        return new AuthenticationResult(
            account.PlayerId,
            account.LoginId,
            account.Role.ToString(),
            token.Value,
            token.ExpiresAt);
    }
}

/// <summary>인증에 성공한 내부 결과입니다.</summary>
/// <param name="PlayerId">토큰을 사용할 Player 식별자입니다.</param>
/// <param name="LoginId">정규화된 로그인 식별자입니다.</param>
/// <param name="Role">인가 정책이 사용할 역할 이름입니다.</param>
/// <param name="AccessToken">서명된 JWT 접근 토큰입니다.</param>
/// <param name="ExpiresAt">토큰 만료 시각입니다.</param>
public sealed record AuthenticationResult(
    Guid PlayerId,
    string LoginId,
    string Role,
    string AccessToken,
    DateTimeOffset ExpiresAt);
