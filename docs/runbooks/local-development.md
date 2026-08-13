# 로컬 개발 환경 실행 절차

이 문서는 개발 PC에서 PostgreSQL·Redis·Orleans Silo·ASP.NET Core API를 시작하고 검증하고 종료하는 절차를 기록합니다. 모든 명령은 `CoopGameServer.slnx`가 있는 저장소 최상위 폴더에서 실행합니다.

## 시작 전 확인

- Docker Desktop이 실행 중이고 `Engine running` 상태여야 합니다.
- 프로젝트 최상위 폴더에 `.env`가 있어야 합니다.
- `.env`는 실제 비밀번호를 담으므로 GitHub에 올리면 안 됩니다.
- PostgreSQL 기본 호스트 포트는 `15432`이며 컨테이너 내부 포트는 `5432`입니다.
- `.env`의 `POSTGRES_HOST_PORT`를 바꾸면 API User Secrets의 `Port`도 같은 값으로 바꿔야 합니다.

## 1. PostgreSQL·Redis 시작

반복되는 시작 절차는 다음 PowerShell 스크립트로 실행합니다.

```powershell
.\tools\Start-LocalEnvironment.ps1
```

스크립트는 다음을 순서대로 수행합니다.

1. Docker Desktop Engine 연결 확인
2. `docker compose up -d` 실행
3. PostgreSQL과 Redis가 `healthy` 상태가 될 때까지 대기
4. `docker compose ps`와 `git status` 출력

`tools\Start-LocalEnvironment.cmd`를 더블클릭해도 같은 스크립트가 실행됩니다. CMD(Command, Windows 명령 프롬프트 배치 파일)는 PowerShell 실행을 연결하는 포장 파일이고, 실제 준비 로직은 PS1(PowerShell Script, PowerShell 스크립트 파일)에 있습니다.

수동으로 시작하려면 다음 명령을 사용합니다.

```powershell
docker compose up -d
docker compose ps
```

- `up`: `compose.yaml`에 정의한 서비스를 생성하거나 시작합니다.
- `-d`: detached mode(디태치드 모드, 터미널을 점유하지 않는 백그라운드 실행)입니다.
- 준비 완료 기준은 두 서비스 모두 `healthy`입니다.

기본 포트 연결:

- PostgreSQL: 호스트 `127.0.0.1:15432` → 컨테이너 `5432`
- Redis: 호스트 `127.0.0.1:6379` → 컨테이너 `6379`

`127.0.0.1`은 loopback address(루프백 주소, 현재 PC 자신만 접근하는 주소)입니다. 개발 DB와 Redis가 같은 네트워크의 다른 PC에 노출되지 않게 합니다.

## 2. API 연결 문자열 저장

처음 실행하거나 PostgreSQL 호스트 포트·비밀번호를 바꿨을 때 User Secrets를 갱신합니다.

```powershell
dotnet user-secrets set "ConnectionStrings:GameDb" "Host=localhost;Port=15432;Database=coopgame;Username=coopgame;Password=<실제비밀번호>" --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

- `<실제비밀번호>`는 `.env`의 `POSTGRES_PASSWORD` 값으로 바꿉니다.
- 비밀번호가 포함된 실제 명령은 화면 공유·문서·Git 커밋에 남기지 않습니다.
- User Secrets는 개발 PC 전용 비밀 저장소이며 배포 환경의 비밀 관리 수단은 아닙니다.

## 3. 데이터베이스 Migration 적용

새 PostgreSQL 볼륨을 만들었거나 Migration이 추가됐다면 실행합니다.

```powershell
dotnet ef database update --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj --startup-project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

`database update`는 아직 적용되지 않은 EF Core Migration을 실행해 테이블·인덱스·제약 조건을 현재 코드와 맞춥니다. API는 시작할 때 Migration을 자동 적용하지 않으므로 새 환경에서는 이 단계가 필요합니다.

EF Core CLI가 없다면 먼저 설치합니다.

```powershell
dotnet tool install --global dotnet-ef
```

## 4. 빌드와 전체 테스트

```powershell
dotnet build CoopGameServer.slnx --configuration Release
dotnet test CoopGameServer.slnx --configuration Release --no-build
```

- 단위 테스트는 메서드·도메인 규칙을 작은 범위에서 검증합니다.
- 통합 테스트는 Testcontainers가 별도의 임시 PostgreSQL 컨테이너를 만들어 실제 UNIQUE·Transaction·행 잠금을 검증합니다.
- 통합 테스트 컨테이너는 Compose 개발 DB와 별개이며 테스트 종료 시 폐기됩니다.
- Docker Engine이 꺼져 있으면 단위 테스트는 가능하지만 통합 테스트는 시작 전 실패합니다.

## 5. Orleans Silo 실행 — PowerShell 창 A

```powershell
dotnet run --project .\src\CoopGameServer.Silo\CoopGameServer.Silo.csproj
```

Silo(사일로)는 Grain을 활성화하고 실행하는 Orleans 서버 프로세스입니다. `Application started` 로그가 나온 뒤 창을 열어 둡니다.

## 6. ASP.NET Core API 실행 — PowerShell 창 B

```powershell
dotnet run --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

API는 Player·Reward HTTP 요청을 처리하고 Orleans Client로 Silo를 호출합니다. API와 Silo는 별도 프로세스이므로 동시에 실행되어야 Ping 경로가 성공합니다.

## 7. Orleans 연결 확인 — PowerShell 창 C

```powershell
Invoke-RestMethod -Uri http://localhost:5265/api/diagnostics/orleans/ping/local-smoke-test
```

`grainId`와 `respondedAtUtc`가 반환되면 API → Orleans Client → Silo → PingGrain 통신이 정상입니다. Ping은 PostgreSQL이나 Redis 데이터를 변경하지 않습니다.

## 상태·로그 확인

```powershell
docker compose ps
docker compose logs
```

- `ps`: 컨테이너 상태와 포트 연결을 표시합니다.
- `logs`: 전체 컨테이너 로그를 표시합니다.
- PostgreSQL만 보려면 `docker compose logs postgres`를 사용합니다.

## 문제 해결

### `'nPolicy'은(는) ... 명령이 아닙니다`

이 오류는 Docker 컨테이너 상태가 아니라 CMD 파일의 문자 인코딩·줄바꿈 해석 문제입니다. 현재 CMD는 ASCII(American Standard Code for Information Interchange, 영문 중심 기본 문자 부호)만 사용하고 `.gitattributes`에서 CRLF(Carriage Return Line Feed, Windows 줄바꿈)를 고정합니다.

### `ports are not available` 또는 호스트 `5432` 바인딩 실패

Windows·WSL(Windows Subsystem for Linux)·Hyper-V가 `5432`를 포함한 포트 범위를 예약할 수 있습니다. 이 프로젝트는 호스트 기본 포트를 `15432`로 사용하고 컨테이너 내부에서만 PostgreSQL 기본 포트 `5432`를 유지합니다.

예약 범위 확인 명령:

```powershell
netsh interface ipv4 show excludedportrange protocol=tcp
```

Windows 예약 범위를 임의로 삭제하지 않습니다. 필요하면 `.env`의 `POSTGRES_HOST_PORT`를 다른 비예약 포트로 변경하고 User Secrets도 같은 포트로 맞춥니다.

### API에서 PostgreSQL 연결 실패

다음을 확인합니다.

1. `docker compose ps`에서 PostgreSQL이 `healthy`인지
2. `.env`의 `POSTGRES_HOST_PORT`와 User Secrets의 `Port`가 같은지
3. User Secrets의 사용자명·DB 이름·비밀번호가 `.env`와 같은지
4. 새 볼륨이라면 `dotnet ef database update`를 실행했는지

## 안전한 종료

1. API와 Silo PowerShell 창에서 각각 `Ctrl+C`를 누릅니다.
2. 컨테이너를 내립니다.

```powershell
docker compose down
```

Compose 네트워크와 컨테이너는 제거되지만 PostgreSQL·Redis 데이터 볼륨은 유지됩니다.

## 개발 데이터 초기화

```powershell
docker compose down -v
```

`-v`는 Volume(볼륨, 컨테이너 밖에 유지되는 데이터 저장 공간)까지 삭제합니다. PostgreSQL·Redis 로컬 데이터가 복구되지 않으므로 초기화가 명확히 필요할 때만 실행합니다.
