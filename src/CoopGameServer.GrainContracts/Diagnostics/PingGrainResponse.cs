using Orleans;

namespace CoopGameServer.GrainContracts.Diagnostics;

/// <summary>
/// <see cref="IPingGrain.PingAsync"/> 호출 결과를 API까지 전달하는 데이터입니다.
/// </summary>
/// <param name="GrainId">
/// 호출된 Grain의 문자열 식별자입니다.
/// 같은 식별자로 얻은 Grain 참조는 같은 논리적 Grain을 가리킵니다.
/// </param>
/// <param name="RespondedAtUtc">
/// Grain 구현체가 응답을 만든 UTC(Coordinated Universal Time, 협정 세계시) 시각입니다.
/// </param>
/// <remarks>
/// GenerateSerializer와 Id는 Orleans가 이 데이터를 API와 Silo 사이에서 안전하게
/// 직렬화(객체를 전송 가능한 데이터 형태로 변환)하도록 돕습니다.
/// </remarks>
[GenerateSerializer]
public sealed record PingGrainResponse(
    [property: Id(0)] string GrainId,
    [property: Id(1)] DateTimeOffset RespondedAtUtc);
