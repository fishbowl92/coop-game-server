# 개발 및 커밋 규칙

이 문서는 이 저장소에 코드를 추가할 때 지킬 최소한의 공통 규칙을 정의합니다. 목적은 코드의 동작과 변경 이력을 다른 사람이 빠르게 이해할 수 있게 하는 것입니다.

## 코드 규칙

- 저장소 루트의 `.editorconfig`를 기준으로 들여쓰기, 줄바꿈, 공백을 통일합니다.
- `Directory.Build.props`는 모든 C# 프로젝트에 공통 적용되는 컴파일 설정입니다.
- `Nullable(널러블, null 값 가능성 추적)` 경고는 가능한 한 수정합니다. 실행 중 `NullReferenceException(널 참조 예외)` 가능성을 일찍 찾기 위함입니다.
- 순수 도메인 규칙을 새로 만들 때는 `tests/CoopGameServer.UnitTests`에 단위 테스트를 함께 작성합니다.
- PostgreSQL의 Transaction·UNIQUE·행 잠금처럼 실제 DB 동작에 의존하면 `tests/CoopGameServer.IntegrationTests`에 통합 테스트를 작성합니다.

## 커밋 메시지

커밋 첫 줄은 `type: 짧은 변경 요약` 형식으로 작성합니다.

자주 사용하는 `type(변경 종류)`는 다음과 같습니다.

- `feat` — feature(기능): 사용자 또는 게임 기능 추가
- `fix` — fix(수정): 버그 수정
- `refactor` — refactor(구조 개선): 동작은 유지하면서 코드 구조 개선
- `test` — test(검증): 테스트 추가·수정
- `docs` — documents(문서): 문서만 변경
- `chore` — chore(자잘한 유지보수 작업): 빌드 설정, 의존성, 개발 환경 변경
- `ci` — Continuous Integration(지속적 통합): 자동 빌드·테스트 설정 변경

예시 명령은 다음과 같습니다.

    git add -A
    git commit -m "test: add reward validation cases"
    git push

- `git add -A`: 새 파일, 수정 파일, 삭제 파일을 이번 커밋 대상으로 등록합니다.
- `git commit -m "..."`: 등록한 변경을 로컬 Git 이력에 하나의 기록으로 남깁니다.
- `git push`: 로컬 커밋을 GitHub 원격 저장소로 전송합니다.

## 변경 전 확인

    dotnet build CoopGameServer.slnx
    dotnet test CoopGameServer.slnx

- `dotnet build`: 솔루션의 모든 프로젝트를 컴파일하여 코드가 빌드되는지 확인합니다.
- `dotnet test`: xUnit.net 단위 테스트와 Testcontainers PostgreSQL 통합 테스트를 실행합니다.
- 통합 테스트는 Compose 개발 DB가 아닌 일회성 PostgreSQL 컨테이너를 만들기 때문에 Docker Desktop Engine이 실행 중이어야 합니다.
