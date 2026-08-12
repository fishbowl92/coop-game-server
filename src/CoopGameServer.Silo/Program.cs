using Microsoft.Extensions.Hosting;

// Silo는 Grain 구현체를 실제로 실행하는 Orleans 서버 프로세스입니다.
// 이 프로젝트는 HTTP 요청을 직접 받지 않습니다. HTTP 요청은 Api 프로젝트가 받고,
// Api가 Grain 메서드를 호출하면 Silo가 해당 Grain을 활성화하여 실행합니다.
var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        // 개발 PC에서만 사용하는 단일 Silo 구성입니다.
        // Orleans의 Silo 간 통신 포트(기본 11111)와 API Client 접속 게이트웨이 포트
        // (기본 30000)를 localhost에 준비합니다. 운영 환경의 클러스터 구성은 아직 범위 밖입니다.
        siloBuilder.UseLocalhostClustering();
    })
    .Build();

await host.RunAsync();
