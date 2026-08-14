namespace CoopGameServer.Api.Authentication;

/// <summary>여러 컨트롤러에서 같은 이름으로 재사용할 인가 정책 이름을 모읍니다.</summary>
public static class AuthorizationPolicies
{
    /// <summary>운영자 역할이 있어야 실행할 수 있는 API 정책 이름입니다.</summary>
    public const string AdministratorOnly = "AdministratorOnly";
}
