# 8주차 부하 시험은 전용 tmpfs DB와 전용 서버 프로세스를 사용합니다.
[CmdletBinding()]
param(
    [ValidateSet('progression', 'combat')][string]$Scenario = 'progression',
    [ValidateRange(1, 20)][int]$Rate = 1,
    [ValidateRange(1, 120)][int]$DurationSeconds = 8,
    [ValidateRange(1, 10)][int]$Repeats = 3,
    [string]$ReportRoot = 'artifacts/week08'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot
$composeFile = Join-Path $repoRoot 'tests/CoopGameServer.LoadTests/compose.yaml'
$composeProject = "coop-week08-$PID"
$reportPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ReportRoot))
New-Item -ItemType Directory -Force -Path $reportPath | Out-Null
$environmentNames = @('LOAD_DB_PASSWORD','ConnectionStrings__GameDb','ConnectionStrings__Redis','Authentication__Jwt__SigningKey','Authentication__Jwt__Issuer','Authentication__Jwt__Audience','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT','ASPNETCORE_URLS','COOP_LOAD_SCENARIO','COOP_LOAD_BASE_URL','COOP_LOAD_DB_CONNECTION','COOP_LOAD_REPORT_DIR','COOP_LOAD_RATE','COOP_LOAD_DURATION_SECONDS','COOP_LOAD_GIT_COMMIT')
$oldEnvironment = @{}
foreach ($name in $environmentNames) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$siloProcess = $null
$apiProcess = $null
function Stop-TestProcesses {
    foreach ($process in @($script:apiProcess, $script:siloProcess)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
    $script:apiProcess = $null
    $script:siloProcess = $null
}
try {
    foreach ($port in @(25432,26379,11111,30000,5266)) {
        if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
            throw "시험 전용 포트 $port가 이미 사용 중입니다."
        }
    }
    $env:LOAD_DB_PASSWORD = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(30))
    $env:ConnectionStrings__GameDb = "Host=127.0.0.1;Port=25432;Database=coop_load;Username=coop_load;Password=$env:LOAD_DB_PASSWORD"
    $env:ConnectionStrings__Redis = '127.0.0.1:26379'
    $env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:Authentication__Jwt__Issuer = 'CoopGameServer.Week08'
    $env:Authentication__Jwt__Audience = 'CoopGameServer.Week08.Client'
    $env:ASPNETCORE_ENVIRONMENT = 'Staging'
    $env:DOTNET_ENVIRONMENT = 'Staging'
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5266'
    $env:COOP_LOAD_SCENARIO = $Scenario
    $env:COOP_LOAD_BASE_URL = 'http://127.0.0.1:5266/'
    $env:COOP_LOAD_DB_CONNECTION = $env:ConnectionStrings__GameDb
    $env:COOP_LOAD_RATE = "$Rate"
    $env:COOP_LOAD_DURATION_SECONDS = "$DurationSeconds"
    $env:COOP_LOAD_GIT_COMMIT = (git rev-parse HEAD).Trim()
    docker compose -p $composeProject -f $composeFile config -q
    if ($LASTEXITCODE -ne 0) { throw 'Compose 설정 검증 실패' }
    dotnet build CoopGameServer.slnx --configuration Release -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Release 빌드 실패' }
    for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
        $runPath = Join-Path $reportPath ("{0}-run{1}" -f $Scenario, $repeat)
        New-Item -ItemType Directory -Force -Path $runPath | Out-Null
        $env:COOP_LOAD_REPORT_DIR = $runPath
        try {
            docker compose -p $composeProject -f $composeFile up -d --wait --wait-timeout 90
            if ($LASTEXITCODE -ne 0) { throw '시험 DB/Redis 시작 실패' }
            dotnet ef database update --project src/CoopGameServer.Persistence --startup-project src/CoopGameServer.Api --configuration Release --no-build *> (Join-Path $runPath 'migration.log')
            if ($LASTEXITCODE -ne 0) { throw '시험 DB Migration 실패' }
            $siloProcess = Start-Process dotnet -ArgumentList @('run','--project','src/CoopGameServer.Silo','--configuration','Release','--no-build','--no-launch-profile') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runPath 'silo.stdout.log') -RedirectStandardError (Join-Path $runPath 'silo.stderr.log')
            $siloReady = $false
            for ($attempt = 0; $attempt -lt 60; $attempt++) {
                if ($siloProcess.HasExited) { throw 'Silo가 시작 중 종료됐습니다. 로그를 확인하세요.' }
                if (Get-NetTCPConnection -State Listen -LocalPort 30000 -ErrorAction SilentlyContinue) {
                    $siloReady = $true
                    break
                }
                Start-Sleep -Seconds 1
            }
            if (-not $siloReady) { throw 'Silo 접속 포트 준비 시간 초과' }
            $apiProcess = Start-Process dotnet -ArgumentList @('run','--project','src/CoopGameServer.Api','--configuration','Release','--no-build','--no-launch-profile') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runPath 'api.stdout.log') -RedirectStandardError (Join-Path $runPath 'api.stderr.log')
            $apiReady = $false
            for ($attempt = 0; $attempt -lt 60; $attempt++) {
                if ($apiProcess.HasExited) { throw 'API가 시작 중 종료됐습니다. 로그를 확인하세요.' }
                if (Get-NetTCPConnection -State Listen -LocalPort 5266 -ErrorAction SilentlyContinue) {
                    $apiReady = $true
                    break
                }
                Start-Sleep -Seconds 1
            }
            if (-not $apiReady) { throw 'API 접속 포트 준비 시간 초과' }
            Write-Host "8주차 $Scenario 반복 $repeat/$Repeats 시작"
            dotnet run --project tests/CoopGameServer.LoadTests --configuration Release --no-build --no-launch-profile
            if ($LASTEXITCODE -ne 0) { throw "반복 $repeat 결과 또는 정합성 검사 실패" }
        }
        finally {
            Stop-TestProcesses
            docker compose -p $composeProject -f $composeFile down | Out-Null
        }
    }
}
finally {
    Stop-TestProcesses
    docker compose -p $composeProject -f $composeFile down | Out-Null
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process')
    }
}
