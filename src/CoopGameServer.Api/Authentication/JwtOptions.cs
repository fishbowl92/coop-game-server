namespace CoopGameServer.Api.Authentication;

/// <summary>
/// JWT(JSON Web Token, 서명된 로그인 토큰)의 발급과 검증에 공통으로 사용할 설정입니다.
/// </summary>
/// <remarks>
/// SigningKey(서명 키)는 토큰 위조를 막는 비밀값이므로 appsettings.json이나 Git에 넣지 않습니다.
/// 개발 PC에서는 User Secrets에만 저장하고, 운영 환경에서는 배포 플랫폼의 비밀 저장소에서 전달합니다.
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>설정 파일과 User Secrets에서 이 값을 찾을 루트 경로입니다.</summary>
    public const string SectionName = "Authentication:Jwt";

    /// <summary>토큰을 발급한 서버 이름입니다.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>이 토큰을 받아 사용할 클라이언트 종류를 구분하는 이름입니다.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>HMAC(Hash-based Message Authentication Code, 해시 기반 메시지 인증 코드) 서명에 쓸 비밀 키입니다.</summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>발급한 접근 토큰의 유효 시간입니다.</summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>구성 공급자에서 필수 설정을 읽고 누락 여부를 즉시 검증합니다.</summary>
    /// <param name="configuration">appsettings, 환경 변수, User Secrets를 합친 설정 객체입니다.</param>
    /// <returns>유효한 JWT 설정입니다.</returns>
    public static JwtOptions FromConfiguration(IConfiguration configuration)
    {
        var issuer = configuration[$"{SectionName}:Issuer"];
        var audience = configuration[$"{SectionName}:Audience"];
        var signingKey = configuration[$"{SectionName}:SigningKey"];

        if (string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(audience)
            || string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Authentication:Jwt 설정이 없습니다. User Secrets에 Issuer, Audience, SigningKey를 설정하세요.");
        }

        // HMAC-SHA256은 예측할 수 없는 256비트(32바이트) 이상의 키를 사용해야 합니다.
        if (System.Text.Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Authentication:Jwt:SigningKey는 최소 32바이트의 예측 불가능한 값이어야 합니다.");
        }

        return new JwtOptions
        {
            Issuer = issuer,
            Audience = audience,
            SigningKey = signingKey,
        };
    }
}
