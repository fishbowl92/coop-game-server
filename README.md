# CoopGameServer

> Orleans(오를리언스) 기반 협동 게임 서버 포트폴리오 프로젝트입니다.

현재는 Player 프로필을 PostgreSQL에 저장하고 HTTP API로 생성·조회·닉네임 변경하는 1주차 단계입니다. 재화·인벤토리·보상·Orleans Grain은 아직 구현하지 않았습니다.

## 현재 구성

- C# / .NET 10: 서버 애플리케이션의 언어와 실행 플랫폼입니다.
- ASP.NET Core: HTTP(Hypertext Transfer Protocol, 웹 요청·응답 통신 규약) API 서버 프레임워크입니다.
- PostgreSQL: 계정, 재화, 인벤토리처럼 정확하고 오래 보관해야 하는 관계형 데이터베이스입니다.
- Redis(REmote DIctionary Server, 원격 딕셔너리 서버): 캐시, 세션, 짧은 수명의 상태를 빠르게 처리하는 메모리 기반 저장소입니다.
- EF Core(Entity Framework Core, 객체와 관계형 데이터베이스를 연결하는 ORM): Player 객체와 PostgreSQL 테이블의 매핑·Migration을 관리합니다.
- Docker Compose: 여러 컨테이너(Container, 격리된 실행 환경)를 하나의 설정 파일로 실행하는 도구입니다.

## 저장소 구조

```text
CoopGameServer/
├── src/
│   ├── CoopGameServer.Api/        # ASP.NET Core API 프로젝트
│   └── CoopGameServer.Contracts/  # 프로젝트 간 요청·응답 형식과 인터페이스 약속
├── tests/
│   └── CoopGameServer.UnitTests/  # xUnit 단위 테스트
├── docs/            # 설계 문서와 복습 자료
├── deploy/          # 배포 관련 설정
├── tools/           # 데이터 검증 등 개발 보조 도구
├── compose.yaml     # PostgreSQL·Redis 컨테이너 설정
├── .env.example     # 공유 가능한 환경 변수 예시
└── .env             # 내 PC 전용 실제 비밀번호, Git에 올리지 않음
```

## 사전 준비

- .NET 10 SDK(Software Development Kit, 개발 도구 모음)
- Docker Desktop
- Git

## 로컬 실행

1. `.env.example`을 참고해 프로젝트 최상위 폴더에 `.env` 파일을 만듭니다.
   `.env`에는 PostgreSQL 비밀번호처럼 공개하면 안 되는 로컬 설정을 넣습니다.

2. PostgreSQL과 Redis를 실행합니다.

   ```powershell
   docker compose up -d
   ```

   `up`은 서비스를 실행하고, `-d`는 detached mode(디태치드 모드, 터미널을 점유하지 않는 백그라운드 실행)입니다.

3. 컨테이너 상태를 확인합니다.

   ```powershell
   docker compose ps
   ```

   PostgreSQL과 Redis가 `healthy` 또는 `running`이면 정상입니다.

4. API의 로컬 DB 연결 문자열을 User Secrets(개발 PC 전용 비밀 저장소)에 설정합니다.

   ```powershell
   dotnet user-secrets set "ConnectionStrings:GameDb" "Host=localhost;Port=5432;Database=coopgame;Username=coopgame;Password=<실제비밀번호>" --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
   ```

   `<실제비밀번호>`에는 `.env`의 `POSTGRES_PASSWORD` 값을 사용합니다. 이 명령은 비밀번호를 Git이 아닌 현재 PC의 User Secrets에 저장합니다.

5. API 프로젝트를 실행합니다.

   ```powershell
   dotnet run --project .\src\CoopGameServer.Api\CoopGameServer.Api.csproj
   ```

   `--project`는 실행할 C# 프로젝트 파일을 직접 지정하는 옵션입니다.

6. 브라우저 또는 PowerShell에서 다음 주소를 확인합니다.

   - `http://localhost:5265/openapi/v1.json`: OpenAPI(API 명세 표준) 문서
   - `POST http://localhost:5265/api/players`: Player 생성
   - `GET http://localhost:5265/api/players/{playerId}`: Player 단건 조회
   - `PATCH http://localhost:5265/api/players/{playerId}/nickname`: Player 닉네임 변경

## 검증 명령

```powershell
dotnet build CoopGameServer.slnx
dotnet test CoopGameServer.slnx
git status
```

- `dotnet build CoopGameServer.slnx`: 솔루션에 포함된 모든 프로젝트를 컴파일하고 패키지·설정 오류를 확인합니다.
- `dotnet test CoopGameServer.slnx`: xUnit 단위 테스트 프로젝트의 자동 테스트를 실행합니다.
- `git status`: 커밋할 파일과 실수로 포함된 비밀 파일이 없는지 확인합니다.

## 설계 문서

- `docs/adr/0001-use-orleans-for-game-entity-coordination.md`: Orleans 선택 이유
- `docs/adr/0002-separate-postgresql-and-redis-responsibilities.md`: PostgreSQL·Redis 책임 분리
- `docs/adr/0003-use-idempotency-keys-for-state-changing-requests.md`: 상태 변경 요청의 멱등성 키 원칙
- `docs/adr/0004-use-ef-core-for-player-persistence.md`: Player 영속성에 EF Core를 선택한 이유
- `docs/architecture/request-cancellation-and-retry.md`: 요청 취소·재시도 원칙

## 다음 목표

2주차에는 player_wallets·inventories·reward_audits를 추가하고, requestId 기반의 멱등성·트랜잭션·중복 보상 방지를 구현합니다.
