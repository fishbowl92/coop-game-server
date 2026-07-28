<#
.SYNOPSIS
    CoopGameServer의 로컬 개발 환경을 시작하고 상태를 확인합니다.

.DESCRIPTION
    이 스크립트는 다음 반복 작업을 순서대로 수행합니다.
    1. Docker Desktop 엔진에 연결할 수 있는지 확인합니다.
    2. compose.yaml의 PostgreSQL과 Redis를 백그라운드에서 시작합니다.
    3. 두 서비스가 healthy(헬스체크 통과) 상태가 될 때까지 기다립니다.
    4. 컨테이너 상태와 Git 작업 트리 상태를 출력합니다.

    Docker Desktop 자체를 자동으로 실행하지는 않습니다.
    Docker Desktop을 먼저 실행하고 Engine running 상태가 된 뒤 사용해야 합니다.
#>

[CmdletBinding()]
param(
    # 컨테이너가 healthy 상태가 될 때까지 기다리는 최대 시간입니다.
    [ValidateRange(10, 300)]
    [int]$HealthTimeoutSeconds = 60
)

# 오류가 발생하면 뒤의 작업을 계속하지 않고 즉시 catch 구문으로 이동합니다.
$ErrorActionPreference = 'Stop'

# 이 스크립트는 tools 폴더 안에 있으므로, 한 단계 위가 프로젝트 루트입니다.
$projectRoot = Split-Path -Path $PSScriptRoot -Parent

function Invoke-ComposeCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    # docker compose 명령을 실행하고, 실패하면 이해하기 쉬운 오류로 중단합니다.
    & docker compose @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') 명령이 실패했습니다."
    }
}

try {
    Set-Location -LiteralPath $projectRoot

    Write-Host "[1/4] Docker Desktop 엔진 연결을 확인합니다..." -ForegroundColor Cyan

    # Docker API에 접속할 수 없다면 Docker Desktop이 아직 실행되지 않은 상태입니다.
    & docker info *> $null

    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Desktop이 실행 중이 아니거나 Engine running 상태가 아닙니다. Docker Desktop을 먼저 실행하세요.'
    }

    Write-Host "[2/4] PostgreSQL과 Redis 컨테이너를 시작합니다..." -ForegroundColor Cyan
    Invoke-ComposeCommand -Arguments @('up', '-d')

    # compose.yaml에 정의된 서비스 이름을 읽어 하드코딩을 줄입니다.
    $services = @(& docker compose config --services)

    if ($LASTEXITCODE -ne 0 -or $services.Count -eq 0) {
        throw 'compose.yaml에서 서비스 목록을 읽지 못했습니다.'
    }

    Write-Host "[3/4] 서비스가 healthy 상태가 될 때까지 기다립니다..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)

    do {
        $serviceStates = @()

        foreach ($service in $services) {
            # 현재 Compose 프로젝트에서 해당 서비스의 컨테이너 ID를 얻습니다.
            $containerId = (& docker compose ps -q $service).Trim()

            if ([string]::IsNullOrWhiteSpace($containerId)) {
                # 변수 이름 바로 뒤의 ':'를 문자열로 확실히 처리하기 위해 ${service} 표기를 사용합니다.
                $serviceStates += "${service}: 컨테이너 없음"
                continue
            }

            # Healthcheck가 있으면 healthy/starting 등을, 없으면 running 상태를 읽습니다.
            $state = (& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $containerId).Trim()
            $serviceStates += "${service}: $state"
        }

        $allHealthy = ($serviceStates.Count -eq $services.Count) -and
            ($serviceStates | ForEach-Object { $_ -match ': healthy$' } | Where-Object { -not $_ }).Count -eq 0

        if (-not $allHealthy -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
    } while (-not $allHealthy -and (Get-Date) -lt $deadline)

    if (-not $allHealthy) {
        Write-Host "서비스 준비 시간이 초과되었습니다: $($serviceStates -join ', ')" -ForegroundColor Yellow
        Write-Host '상세 원인은 docker compose logs 명령으로 확인하세요.' -ForegroundColor Yellow
        exit 1
    }

    Write-Host "[4/4] 컨테이너와 Git 상태를 출력합니다." -ForegroundColor Cyan
    Invoke-ComposeCommand -Arguments @('ps')

    Write-Host "`nGit 작업 트리 상태:" -ForegroundColor Cyan
    & git status

    if ($LASTEXITCODE -ne 0) {
        throw 'git status 명령이 실패했습니다.'
    }

    Write-Host "`n로컬 개발 환경 준비가 완료되었습니다." -ForegroundColor Green
}
catch {
    Write-Host "`n로컬 개발 환경 준비에 실패했습니다: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
