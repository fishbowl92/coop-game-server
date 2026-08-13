# CoopGameServer

> Microsoft Orleans 기반 협동 게임 서비스 백엔드를 단계적으로 구현하는 C#·.NET 10 프로젝트입니다.

현재는 Player 프로필, PostgreSQL 영속성, 재화·인벤토리 보상, 멱등성·트랜잭션, PostgreSQL 통합 테스트와 API → Orleans Client → Silo → Ping Grain 연결까지 구현했습니다. 3주차의 다음 게임 기능은 PartyGrain이며, Redis 애플리케이션 연동·인증·운영 배포는 아직 구현하지 않았습니다.

## 현재 구현 범위

- Player 생성·조회·닉네임 변경 HTTP API
- PostgreSQL의 `players`, `player_wallets`, `inventories`, `reward_audits` 테이블
- EF Core(Entity Framework Core, C# 객체와 관계형 데이터베이스를 연결하는 ORM) Migration
- `requestId` 기반 보상 멱등성(Idempotency, 같은 요청을 재전송해도 한 번만 반영되는 성질)
- 보상 이력·지갑·인벤토리를 함께 처리하는 Transaction(트랜잭션, 모두 성공하거나 모두 실패하는 작업 단위)
- Player 행 잠금을 이용한 서로 다른 동시 보상의 유실 갱신 방지
- xUnit 단위 테스트와 Testcontainers 기반 실제 PostgreSQL 통합 테스트
- 별도 Orleans Silo와 진단용 Ping Grain 호출
- GitHub Actions CI(Continuous Integration, 지속적 통합) 빌드·테스트

Redis(REmote DIctionary Server, 원격 딕셔너리 서버)는 현재 로컬 컨테이너만 준비되어 있습니다. 캐시·TTL(Time To Live, 자동 만료 시간)·장애 시 PostgreSQL 대체 경로는 5주차에 구현할 계획입니다.

## 요청 흐름

```text
HTTP Client
    ├─ Player·Reward API ──> ASP.NET Core ──> EF Core ──> PostgreSQL
    └─ Orleans Ping API ──> Orleans Client ──> Silo ──> PingGrain

Redis: 컨테이너만 준비됨, 애플리케이션 연결은 아직 없음
```

## 저장소 구조

```text
CoopGameServer/
├── src/
│   ├── CoopGameServer.Api/             # ASP.NET Core API, EF Core, 보상 서비스
│   ├── CoopGameServer.Contracts/       # HTTP 요청·응답 계약
│   ├── CoopGameServer.GrainContracts/  # Orleans Grain 호출 계약
│   ├── CoopGameServer.Grains/          # Grain 구현체
│   └── CoopGameServer.Silo/            # Orleans Grain 실행 호스트
├── tests/
│   ├── CoopGameServer.UnitTests/        # 도메인·Controller 단위 테스트
│   └── CoopGameServer.IntegrationTests/ # Testcontainers PostgreSQL 통합 테스트
├── docs/                                # ADR, 아키텍처, 실행 절차
├── deploy/                              # 후속 배포 설정 위치
├── tools/                               # 로컬 환경 자동화 도구
├── compose.yaml                         # PostgreSQL·Redis 컨테이너 설정
├── .env.example                         # 공유 가능한 환경 변수 형식
└── .env                                 # 내 PC 전용 값, Git에 포함하지 않음
```

## 사전 준비

- .NET 10 SDK(Software Development Kit, 개발 도구 모음)
- Docker Desktop과 실행 중인 Docker Engine
- Git
- EF Core CLI(Command-Line Interface, 명령줄 도구)

EF Core CLI가 없다면 한 번만 설치합니다.

```powershell
dotnet tool install --global dotnet-ef
```

`dotnet tool install`은 .NET 전역 도구를 현재 사용자 계정에 설치하고, `dotnet-ef`는 Migration 생성·적용 명령을 제공합니다.

## 처음 실행

모든 명령은 `CoopGameServer.slnx`가 있는 저장소 최상위 폴더에서 실행합니다.

### 1. 로컬 환경 변수 준비

`.env.example`을 복사해 `.env`를 만들고 `POSTGRES_PASSWORD`를 본인만 아는 긴 로컬 비밀번호로 바꿉니다.

```powershell
Copy-Item .env.example .env
```

기본 포트는 다음과 같습니다.

- PostgreSQL: Windows 호스트 `127.0.0.1:15432` → 컨테이너 `5432`
- Redis: Windows 호스트 `127.0.0.1:6379` → 컨테이너 `6379`

`POSTGRES_HOST_PORT`를 바꾸면 아래 User Secrets 연결 문자열의 `Port`도 같은 값으로 맞춰야 합니다.

### 2. PostgreSQL·Redis 시작

Docker Desktop을 먼저 실행한 뒤 자동화 스크립트를 사용합니다.

```powershell
.\tools\Start-LocalEnvironment.ps1
```

스크립트는 Docker Engine 확인 → `docker compose up -d` → 두 컨테이너의 `healthy` 상태 대기 → 컨테이너·Git 상태 출력을 수행합니다. `tools\Start-LocalEnvironment.cmd`를 더블클릭해도 같은 PowerShell 스크립트가 실행됩니다.

### 3. API의 비밀 연결 문자열 저장

```powershell
dotnet user-secrets set "ConnectionStrings:GameDb" "Host=localhost;Port=15432;Database=coopgame;Username=coopgame;Password=<실제비밀번호>" --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

`<실제비밀번호>`를 `.env`의 실제 `POSTGRES_PASSWORD`로 바꿉니다. User Secrets는 값을 Git 저장소가 아니라 현재 Windows 사용자 영역에 저장합니다.

### 4. PostgreSQL 스키마 적용

```powershell
dotnet ef database update --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj --startup-project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

이 명령은 `Data/Migrations`의 Migration을 PostgreSQL에 순서대로 적용해 현재 코드가 요구하는 테이블·인덱스·제약 조건을 만듭니다. 새 데이터 볼륨에서는 반드시 한 번 실행해야 합니다.

### 5. Release 빌드와 자동 테스트

```powershell
dotnet build CoopGameServer.slnx --configuration Release
dotnet test CoopGameServer.slnx --configuration Release --no-build
```

- `build`는 솔루션의 모든 프로젝트를 Release 구성으로 컴파일합니다.
- `test`는 단위 테스트와 Testcontainers PostgreSQL 통합 테스트를 실행합니다.
- `--no-build`는 바로 앞에서 만든 Release 결과물을 재사용합니다.
- 통합 테스트는 Compose의 개발 DB와 별도의 임시 PostgreSQL 컨테이너를 생성하므로 Docker Engine이 필요합니다.

### 6. Orleans Silo 실행 — PowerShell 창 A

```powershell
dotnet run --project .\src\CoopGameServer.Silo\CoopGameServer.Silo.csproj
```

Silo(사일로)는 Orleans Grain을 메모리에서 실행하는 서버 프로세스입니다. 현재 로컬 개발에서는 Silo 포트 `11111`과 Gateway(게이트웨이, Client 연결 입구) 포트 `30000`을 사용합니다.

### 7. API 실행 — PowerShell 창 B

```powershell
dotnet run --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
```

API와 Silo는 서로 다른 프로세스이므로 두 PowerShell 창을 모두 열어 둡니다. 종료할 때는 각 창에서 `Ctrl+C`를 누릅니다.

### 8. 동작 확인 — PowerShell 창 C 또는 브라우저

- `http://localhost:5265/openapi/v1.json`: OpenAPI(API 명세 표준) 문서
- `POST /api/players`: Player 생성
- `GET /api/players/{playerId}`: Player 단건 조회
- `PATCH /api/players/{playerId}/nickname`: Player 닉네임 변경
- `POST /api/players/{playerId}/rewards`: 재화·아이템 보상 지급
- `GET /api/diagnostics/orleans/ping/{grainId}`: API에서 Silo의 Ping Grain 호출

Orleans 연결 확인 명령:

```powershell
Invoke-RestMethod -Uri http://localhost:5265/api/diagnostics/orleans/ping/local-smoke-test
```

`grainId`와 `respondedAtUtc`가 반환되면 API → Orleans Client → Silo → Grain 경로가 연결된 것입니다.

## 일상 검증 명령

```powershell
dotnet build CoopGameServer.slnx --configuration Release
dotnet test CoopGameServer.slnx --configuration Release --no-build
dotnet list CoopGameServer.slnx package --vulnerable --include-transitive
git status
```

- `dotnet list ... --vulnerable`: 직접·간접 NuGet 패키지의 알려진 보안 취약점을 조회합니다.
- `git status`: 커밋 대상과 비밀 파일의 실수 포함 여부를 확인합니다.

## 안전한 종료

API와 Silo를 `Ctrl+C`로 종료한 뒤 필요하면 컨테이너를 내립니다.

```powershell
docker compose down
```

데이터 볼륨은 유지됩니다. `docker compose down -v`는 로컬 PostgreSQL·Redis 데이터를 함께 삭제하므로 데이터 초기화가 명확히 필요할 때만 사용합니다.

## 설계 문서

- `docs/adr/0001-use-orleans-for-game-entity-coordination.md`: Orleans 선택 이유
- `docs/adr/0002-separate-postgresql-and-redis-responsibilities.md`: PostgreSQL·Redis 책임 분리
- `docs/adr/0003-use-idempotency-keys-for-state-changing-requests.md`: 상태 변경 요청의 멱등성 키 원칙
- `docs/adr/0004-use-ef-core-for-player-persistence.md`: Player 영속성에 EF Core를 선택한 이유
- `docs/architecture/request-cancellation-and-retry.md`: 요청 취소·재시도 원칙
- `docs/runbooks/local-development.md`: 로컬 실행·문제 해결 절차

## 현재 한계와 다음 목표

- PingGrain은 연결 진단용이며 아직 게임 상태를 저장하지 않습니다.
- 인증(Authentication, 신원 확인)과 인가(Authorization, 권한 확인)는 아직 없습니다.
- Redis는 컨테이너만 있으며 애플리케이션 코드에서 사용하지 않습니다.
- 다음 기능은 PartyGrain 생성·가입·탈퇴·리더 승계와 TestCluster 자동 테스트입니다.
