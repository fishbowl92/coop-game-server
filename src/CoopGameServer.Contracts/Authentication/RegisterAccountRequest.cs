namespace CoopGameServer.Contracts.Authentication;

/// <summary>
/// 새 플레이어 계정과 연결된 Player를 함께 만드는 회원 가입 요청입니다.
/// </summary>
/// <param name="LoginId">로그인에 사용할 영문·숫자·밑줄 식별자입니다.</param>
/// <param name="Password">전송 중인 비밀번호 원문입니다. 서버는 이를 저장하지 않고 즉시 해시로 변환합니다.</param>
/// <param name="Nickname">게임 안에서 표시할 Player 닉네임입니다.</param>
public sealed record RegisterAccountRequest(string LoginId, string Password, string Nickname);
