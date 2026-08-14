using CoopGameServer.Api.Application.Authentication;
using CoopGameServer.Contracts.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoopGameServer.Api.Controllers;

/// <summary>회원 가입과 로그인을 통해 JWT 접근 토큰을 발급하는 HTTP API입니다.</summary>
/// <remarks>
/// 이 컨트롤러의 두 엔드포인트만은 아직 로그인 전에도 호출해야 하므로 AllowAnonymous를 명시합니다.
/// 나머지 게임 데이터 변경 API는 JWT를 제시해야 실행할 수 있습니다.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/auth")]
public sealed class AuthenticationController(AuthenticationService authenticationService) : ControllerBase
{
    /// <summary>Player와 Account를 함께 생성한 뒤 바로 사용할 접근 토큰을 반환합니다.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthenticationResponse>> Register(
        [FromBody] RegisterAccountRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticationResult result;

        try
        {
            result = await authenticationService.RegisterAsync(request, cancellationToken);
        }
        catch (ArgumentNullException exception)
        {
            return ValidationProblem(detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ArgumentException exception)
        {
            return ValidationProblem(detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicateAccountException exception)
        {
            return Conflict(new ProblemDetails
            {
                Title = exception.Field == "loginId"
                    ? "Login ID already exists."
                    : "Nickname already exists.",
                Detail = exception.Field == "loginId"
                    ? "이미 사용 중인 로그인 식별자입니다."
                    : "이미 사용 중인 닉네임입니다.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return StatusCode(StatusCodes.Status201Created, ToResponse(result));
    }

    /// <summary>로그인 식별자와 비밀번호 해시 검증에 성공할 때만 새 접근 토큰을 반환합니다.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticationResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.LoginAsync(request, cancellationToken);

        // 존재하지 않는 로그인 ID와 틀린 비밀번호에 같은 401을 사용해 계정 존재 여부를 불필요하게 노출하지 않습니다.
        return result is null
            ? Unauthorized(new ProblemDetails
            {
                Title = "Login failed.",
                Detail = "로그인 식별자 또는 비밀번호가 올바르지 않습니다.",
                Status = StatusCodes.Status401Unauthorized,
            })
            : Ok(ToResponse(result));
    }

    /// <summary>내부 인증 결과를 외부 API 계약으로 변환합니다.</summary>
    private static AuthenticationResponse ToResponse(AuthenticationResult result)
    {
        return new AuthenticationResponse(
            result.PlayerId,
            result.LoginId,
            result.Role,
            result.AccessToken,
            result.ExpiresAt);
    }
}
