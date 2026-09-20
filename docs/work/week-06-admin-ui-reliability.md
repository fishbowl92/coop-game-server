# 6주차 관리자 화면 실패 처리 보완

검증 기준일: 2026-09-21 (Asia/Seoul). 기준 소스는 `4d36c9d`에 이 문서와 같은 커밋의 수정 사항을 적용한 상태다.
학습 센터와 주차별 학습 노트는 수정하지 않았다. 이 문서는 구현·검증 인수인계 기록이다.

## 수정한 동작

| 상황 | 변경 전 | 변경 후 |
|---|---|---|
| 지급 응답은 성공했지만 후속 진행도·이력 조회 실패 | 입력과 요청 ID가 초기화되고 실패 메시지만 표시 | 확정 영수증을 유지하고 조회 실패를 별도 안내. 최신 조회 전 새 지급 차단 |
| A 조회 후 B 조회의 일부 단계 실패 | B의 기본 정보와 A의 잔액·이력이 섞일 수 있음 | 조회 시작 시 이전 선택 해제. 세 조회가 모두 성공한 결과만 한꺼번에 반영 |
| 응답 유실 뒤 입력 수정·대상 변경 | 같은 요청 ID로 다른 보상을 재전송할 수 있음 | 최초 관리자·대상·본문을 함께 고정. 화면 편집·대상 변경 차단, 재시도는 보관한 요청 사용 |

지급 확정 후의 **정보 새로고침**은 조회만 수행한다. 새 보상 POST를 보내지 않는다.
미확정 요청에서 로그아웃하더라도 같은 Blazor 회로 안에서는 요청을 유지한다.
원래 관리자로 재로그인해야 해당 요청의 결과를 확인할 수 있다.

## 거절과 불확실한 결과 구분

- 최초 시도의 400·401·403·404는 현재 API 계약에서 영구 변경 전 거절이므로 폼 편집을 다시 허용한다.
- 네트워크 오류·시간 초과·서버 오류·일치하지 않는 성공 영수증은 확정을 보류하고 원래 요청을 유지한다.
- 응답 유실 뒤 재시도가 401·403 등으로 거절되어도 최초 지급 여부는 알 수 없다. 이 경우 요청을 해제하지 않는다.
- 지급 성공 응답은 요청 ID·대상·금액·아이템·사유가 고정한 요청과 일치하는지 확인한 뒤 채택한다.
- 조회·지급·로그인 중 중복 실행, 대상 변경, 로그아웃을 차단한다.

## 파일·함수·상태 변경

| 파일 | 추가·변경·이동 | 이유 |
|---|---|---|
| `src/CoopGameServer.Admin/Services/AdminOperationsState.cs` | 새 화면 조정 클래스. `LookupAsync(query)`, `ReadSelectionAsync(player)` | 조회 결과를 임시 값으로 만든 뒤 `AdminPlayerSelection`으로 함께 반영 |
| 같은 파일 | `GrantAsync()`, `RefreshAsync()`, `LastReceipt`, `RequiresRefresh` | 지급 확정과 조회 실패를 분리하고 조회 재시도에서 추가 지급 방지 |
| 같은 파일 | `RunAsync(action)`, `IsBusy`, `CanLookup/CanGrant/CanEditReward/CanRefresh` | 버튼뿐 아니라 호출 경계에서도 중복 실행·상태 불일치 차단 |
| 같은 파일 | `SignInAsync(loginId, password)`, `SignOut()`, `IsPendingForCurrentAdministrator` | 계정 전환 시 조회 정보 제거, 원래 계정의 미확정 요청은 보존 |
| `src/CoopGameServer.Admin/Services/AdminRewardSubmissionState.cs` | `CreateRequest()` 제거 → `BeginSubmission(playerId, administratorLoginId)`와 `PendingAdminReward` 추가 | 매번 편집값에서 본문을 만드는 대신 최초 요청을 고정 |
| 같은 파일 | `Pending`, `ReleaseRejectedSubmission()`, `ConfirmSuccess()` 변경 | 명확한 최초 거절은 수정 허용, 성공 확정은 요청 해제·새 ID 발급 |
| `src/CoopGameServer.Admin/Components/Administration/AdminOperationsPanel.razor` | 실제 운영 화면을 분리. 영수증·미확정 요청·조회 전용 새로고침 추가 | 동일 운영 화면을 HTML 렌더링 테스트에서 검증 |
| `src/CoopGameServer.Admin/Components/Pages/Home.razor` | 서버 상호작용 페이지 진입점 유지. 기존 `player/progression/history/busy/message` 및 지급·갱신 처리 이동 | 화면과 상태 처리의 중복 소유 제거 |
| `src/CoopGameServer.Admin/Program.cs` | `AdminOperationsState`를 회로 범위 서비스로 등록 | 동일 회로의 로그인·보상·화면 상태 연결 |
| `src/CoopGameServer.Admin/wwwroot/app.css` | 영수증·미확정·경고·입력 잠금 표시 | 지급 성공과 조회 실패가 구분되도록 표시 |
| `tests/CoopGameServer.UnitTests/Admin/` | `AdminOperationsStateTests` 추가, 기존 보상 폼 테스트 변경 | 실패 응답·비동기 중복 클릭·계정 변경·실제 Razor 표시를 회귀 검증 |

서버 API, Grain, PostgreSQL 쓰기·감사·멱등성 계약과 Redis 캐시 정책은 변경하지 않았다.
새 패키지는 추가하지 않았다.

## 검증

- `dotnet build CoopGameServer.slnx --configuration Release --no-incremental --verbosity minimal`: 경고 0, 오류 0.
- `dotnet test CoopGameServer.slnx --configuration Release --no-build --verbosity minimal`: 단위 138개, 통합 138개, 총 276개 통과. 건너뜀·실패 없음.
- 관리자 관련 테스트 22개 통과. 기존 2개를 보강하고 실패 경로·화면 렌더링 검증 20개를 추가했다.
- 수정한 C# 파일 5개만 대상으로 서식 검증 통과. 명령 요약: `dotnet format`의 `--verify-no-changes --no-restore --include` 옵션 사용.
- Windows 테스트 프로세스에만 `DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine`를 지정했다. 개발 데이터 대신 기존 Testcontainers의 일회성 데이터베이스를 사용했다.
- 테스트 결과는 `%TEMP%\CoopGameServer-week6-admin-fix-20260921`에 TRX와 로그로 보관했다. 원격 CI는 이번 수정에 대해 실행하지 않았다.

추가한 관리 화면 테스트는 실제 `AdminApiClient`에 통제된 HTTP 응답을 넣는다.
진행도·이력 조회 실패, 응답 유실 후 본문 보존, 최초 거절과 재시도 거절의 구분,
지급 도중 중복 클릭·조회·로그아웃 차단, 원래 관리자 재로그인, 영수증 불일치를 확인한다.
운영 화면 구성요소를 HTML로 렌더링해 확정 영수증과 조회 경고가 함께 보이는지,
미확정 요청에서 원래 금액과 재시도 버튼이 보이고 편집 폼이 사라지는지도 확인한다.

이는 브라우저 자동화나 실제 네트워크 장애를 재현한 결과는 아니다.
기존 실제 HTTP·Orleans·PostgreSQL·Redis 통합 테스트는 전체 회귀에서 별도로 검증한다.

## 범위와 후속 기록

- 상태 보존 범위는 기존 설계와 같은 Blazor Server 회로다. 브라우저 새로고침으로 새 회로가 생기거나 서버가 재시작되면 미확정 요청을 복원하지 않는다. 회로 밖 영구 요청 복원은 별도 설계가 필요하다.
- 권한 회수·장기 미확정 충돌을 운영자가 해결하는 별도 기능은 이번 수정에 포함하지 않았다.
- 학습 센터는 사용자 요청에 따라 보류했다. 전체 주차 완료 뒤 성공/조회 상태 분리, 대상 스냅샷, 재시도 요청 고정과 이번 검증 사례를 6주차 설명에 반영한다.
- 이 수정은 로컬 커밋 범위이며 원격 푸시와 새 원격 CI 검증은 수행하지 않는다.
