using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using CoopGameServer.Domain.Accounts;

namespace CoopGameServer.Api.Authentication;

/// <summary>
/// 인증된 JWT의 Claim(클레임, 토큰 안에 담긴 신원 정보)에서 현재 Player를 안전하게 읽는 도우미입니다.
/// </summary>
public static class CurrentPlayerClaims
{
    /// <summary>
    /// 현재 요청의 토큰이 지정한 Player를 조작할 권한이 있는지 확인합니다.
    /// </summary>
    /// <param name="user">ASP.NET Core가 검증한 현재 사용자 ClaimsPrincipal입니다.</param>
    /// <param name="targetPlayerId">URL 또는 요청 본문에 들어온 조작 대상 Player 식별자입니다.</param>
    /// <returns>본인 Player이거나 관리자이면 true입니다.</returns>
    public static bool CanAccessPlayer(this ClaimsPrincipal user, Guid targetPlayerId)
    {
        if (user.IsInRole(AccountRole.Administrator.ToString()))
        {
            return true;
        }

        if (targetPlayerId == Guid.Empty)
        {
            return false;
        }

        // JwtBearer의 기본 Claim 매핑은 JWT의 sub를 NameIdentifier로 바꿉니다.
        // 테스트나 설정에 따라 매핑을 끈 환경도 안전하게 지원하도록 두 표현을 모두 읽습니다.
        var rawPlayerId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(rawPlayerId, out var authenticatedPlayerId)
            && authenticatedPlayerId == targetPlayerId;
    }
}
