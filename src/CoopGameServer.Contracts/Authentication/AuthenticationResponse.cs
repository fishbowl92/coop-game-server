namespace CoopGameServer.Contracts.Authentication;

/// <summary>로그인 또는 회원 가입 성공 후 반환하는 인증 결과입니다.</summary>
/// <param name="PlayerId">토큰을 사용해 조작할 Player의 식별자입니다.</param>
/// <param name="LoginId">정규화되어 저장된 로그인 식별자입니다.</param>
/// <param name="Role">서버가 토큰에 기록한 계정 역할입니다.</param>
/// <param name="AccessToken">Authorization 헤더에 넣을 JWT 접근 토큰입니다.</param>
/// <param name="ExpiresAt">접근 토큰이 만료되는 UTC 기준 시각입니다.</param>
public sealed record AuthenticationResponse(
    Guid PlayerId,
    string LoginId,
    string Role,
    string AccessToken,
    DateTimeOffset ExpiresAt);
