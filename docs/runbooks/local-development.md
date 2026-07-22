# 로컬 개발 환경 실행 절차

이 문서는 개발 PC에서 PostgreSQL과 Redis를 시작·확인·종료하는 절차를 기록합니다.

## 시작 전 확인

- Docker Desktop이 실행 중이고 `Engine running` 상태여야 합니다.
- 프로젝트 최상위 폴더에 `.env`가 있어야 합니다.
- `.env`는 실제 비밀번호를 담으므로 GitHub에 올리면 안 됩니다.

## 서비스 시작

```powershell
docker compose up -d
```

이 명령은 `compose.yaml`을 읽어 PostgreSQL과 Redis 컨테이너를 생성하거나 다시 시작합니다. `-d`는 detached mode(디태치드 모드)로, 명령이 끝난 뒤에도 컨테이너가 백그라운드에서 실행되게 합니다.

## 상태 확인

```powershell
docker compose ps
```

각 컨테이너의 이름, 실행 상태, 포트 연결을 확인합니다.

- PostgreSQL: 기본 포트 5432
- Redis: 기본 포트 6379
- `healthy`: Docker healthcheck(헬스체크, 서비스 준비 상태 검사)를 통과한 상태

## 로그 확인

```powershell
docker compose logs
```

컨테이너가 시작되지 않거나 오류가 의심될 때 실행 로그를 봅니다. 한 서비스만 확인하려면 `docker compose logs postgres`처럼 서비스 이름을 뒤에 붙입니다.

## 안전한 종료

```powershell
docker compose down
```

컨테이너와 Compose 네트워크를 멈추고 제거합니다. PostgreSQL과 Redis의 데이터 볼륨(Volume, 컨테이너 밖에 보존되는 저장 공간)은 남으므로, 다음 실행 때 데이터를 다시 사용할 수 있습니다.

## 개발 데이터 초기화

```powershell
docker compose down -v
```

`-v`는 volumes(볼륨)까지 제거한다는 뜻입니다. PostgreSQL·Redis의 로컬 데이터도 삭제됩니다. 개발 초기에만 사용하며, 데이터가 필요한 상황에서는 실행하지 않습니다.

## API 실행

```powershell
dotnet run --project .\CoopGameServer\CoopGameServer.csproj
```

ASP.NET Core API를 실행합니다. 현재 프로젝트 최상위 폴더에는 솔루션 파일만 있으므로 `--project` 옵션으로 실제 C# 프로젝트 파일을 지정합니다.
