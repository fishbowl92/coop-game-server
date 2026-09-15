# 개발 및 커밋 규칙

이 문서는 사람과 에이전트가 함께 쓰는 코드 형식·커밋 규칙을 정의합니다.
에이전트의 작업 순서는 [AGENTS.md](../AGENTS.md)와 [영어 실행 절차](engineering/agent-workflow.md),
규칙의 목적은 [한국어 해설](engineering/agent-workflow.ko.md), 재개 지점은 [현재 작업 기록](work/current.md)에서 확인합니다.

## 코드 규칙

- `.editorconfig`를 기준으로 들여쓰기, 줄바꿈, 공백을 통일합니다.
- `Directory.Build.props`의 공통 컴파일 설정을 따릅니다.
- Nullable(널러블, null 값 가능성 추적) 경고를 해소해 실행 중 null 참조 오류를 예방합니다.
- 순수 도메인 규칙은 `tests/CoopGameServer.UnitTests`에서 검증합니다.
- PostgreSQL의 Transaction(트랜잭션, 여러 변경의 전체 성공·취소 단위), UNIQUE(고유성 제약), 행 잠금에 의존하는 동작은 `tests/CoopGameServer.IntegrationTests`에서 실제 데이터베이스로 검증합니다.
- 추가·변경한 함수와 의미 있는 매개변수·상태에는 역할, 제약, 실패 순서를 설명하는 주석을 작성합니다.

## 커밋 규칙

커밋 첫 줄은 한국어로 변경 기능이나 목적을 설명하고 끝에 마침표를 붙이지 않습니다.
예: `파티 생성, 조회, 가입, 탈퇴, 해산 HTTP API 구현`.
기존 사용자 변경과 미추적 자료를 이번 작업에 섞지 않습니다. 관련 파일만 명시적으로 스테이징한 뒤 검토합니다.

다음은 개발 규칙 문서 하나를 커밋하는 예시입니다. 실제 작업에는 해당 파일 경로와 목적을 사용합니다.

```powershell
git add -- docs/contributing.md
git diff --cached
git diff --cached --check
git commit -m "개발 및 커밋 작업 규칙 정리"
```

- `git add --`: 지정한 파일만 커밋 대상으로 등록합니다.
- `git diff --cached`: 등록한 내용에 관련 없는 변경이나 비밀값이 없는지 확인합니다.
- `git diff --cached --check`: 등록한 변경의 공백 오류를 확인합니다.
- `git commit -m`: 검토한 변경을 로컬 Git 이력으로 저장합니다.

```powershell
git push
```

`git push`는 로컬 커밋을 원격 저장소로 전송합니다. 커밋·푸시·게시는 현재 요청과 기존에 허용된 범위에 따라 수행하며,
이 예시가 실행 자체를 지시하는 것은 아닙니다.

## 변경 검증

코드 변경 전에는 현재 작업 트리와 관련 기존 검증 결과를 확인하고, 변경 규모에 맞는 기준 검사를 실행합니다.
코드 기능 완료 시에는 다음 명령을 저장소 루트에서 하나씩 실행합니다.

```powershell
dotnet build CoopGameServer.slnx --configuration Release
dotnet test CoopGameServer.slnx --configuration Release --no-build
```

- `dotnet build`: 솔루션의 모든 프로젝트를 Release 구성으로 컴파일합니다.
- `dotnet test`: 바로 앞에서 빌드한 동일한 코드의 결과물로 단위·통합 테스트를 실행합니다.
- 통합 테스트는 Compose 개발 데이터베이스와 별도의 임시 PostgreSQL 컨테이너를 사용하므로 Docker 엔진이 필요합니다.

설명·링크·에이전트 지침만 바꾼 작업은 링크·일관성·변경 차이를 검사하고 애플리케이션 테스트를 기본적으로 다시 실행하지 않습니다.
실행 명령·설정 자체가 달라지면 해당 변경의 검증을 추가합니다. 상세한 검증 범위는 영어 실행 절차의 표를 따릅니다.
