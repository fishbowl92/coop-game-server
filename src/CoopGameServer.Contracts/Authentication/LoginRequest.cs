namespace CoopGameServer.Contracts.Authentication;

/// <summary>로그인 토큰을 발급받기 위한 계정 인증 요청입니다.</summary>
/// <param name="LoginId">회원 가입 때 등록한 로그인 식별자입니다.</param>
/// <param name="Password">검증할 비밀번호 원문입니다.</param>
public sealed record LoginRequest(string LoginId, string Password);
