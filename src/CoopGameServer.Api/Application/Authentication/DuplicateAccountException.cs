namespace CoopGameServer.Api.Application.Authentication;

/// <summary>회원 가입에서 이미 사용 중인 로그인 식별자 또는 닉네임을 발견했을 때 발생합니다.</summary>
/// <param name="field">중복된 입력 항목의 이름입니다.</param>
public sealed class DuplicateAccountException(string field) : Exception
{
    /// <summary>중복된 입력 항목의 이름입니다.</summary>
    public string Field { get; } = field;
}
