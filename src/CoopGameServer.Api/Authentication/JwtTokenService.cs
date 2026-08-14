using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CoopGameServer.Domain.Accounts;
using Microsoft.IdentityModel.Tokens;

namespace CoopGameServer.Api.Authentication;

/// <summary>검증된 Account 정보를 바탕으로 JWT 접근 토큰을 발급합니다.</summary>
public sealed class JwtTokenService(JwtOptions options)
{
    private readonly JwtOptions _options = options;

    /// <summary>
    /// Account가 조작할 Player와 역할을 담은 서명 토큰을 만듭니다.
    /// </summary>
    /// <param name="account">비밀번호 검증까지 끝난 계정입니다.</param>
    /// <returns>토큰 문자열과 만료 시각입니다.</returns>
    public IssuedAccessToken CreateAccessToken(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(_options.AccessTokenLifetime);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        // sub(subject, 토큰 주체)는 "이 요청으로 조작할 PlayerId"로 고정합니다.
        // JwtBearer가 이를 NameIdentifier로 매핑하므로 컨트롤러는 URL의 playerId와 한 번만 비교하면 됩니다.
        // AccountId는 로그인 이력 등에서만 쓸 별도 account_id 클레임으로 보관합니다.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.PlayerId.ToString()),
            new Claim("account_id", account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.LoginId),
            new Claim(ClaimTypes.Role, account.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: signingCredentials);

        return new IssuedAccessToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt);
    }
}

/// <summary>새로 발급한 접근 토큰 문자열과 만료 시각을 함께 보관합니다.</summary>
/// <param name="Value">Authorization 헤더에 넣을 JWT 문자열입니다.</param>
/// <param name="ExpiresAt">이 토큰이 더는 받아들여지지 않는 UTC 기준 시각입니다.</param>
public sealed record IssuedAccessToken(string Value, DateTimeOffset ExpiresAt);
