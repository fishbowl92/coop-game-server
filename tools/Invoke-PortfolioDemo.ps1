<#
.SYNOPSIS
    실행 중인 CoopGameServer 전체 스택에서 대표 포트폴리오 흐름을 재현합니다.

.DESCRIPTION
    네 일반 계정의 가입, 4인 파티, 매칭, 게임 방 연결, 전투 시작과 첫 공격,
    관리자 로그인, 보상 지급, 운영 조회를 실제 HTTP 경로로 순서대로 실행합니다.
    JWT와 비밀번호는 출력하지 않습니다. 실행마다 고유 계정을 만들므로 기존 데모 데이터와 충돌하지 않습니다.
#>

[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:5265',

    [Parameter(Mandatory)]
    [string]$AdministratorLoginId,

    [Parameter(Mandatory)]
    [string]$AdministratorPassword
)

$ErrorActionPreference = 'Stop'
$queueKey = 'coop-dungeon-normal-v1'
$demoPassword = 'PortfolioDemo!42'
$suffix = ([Guid]::NewGuid().ToString('N')).Substring(0, 10)

function Invoke-ApiPost {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Body,

        [string]$AccessToken
    )

    $parameters = @{
        Method = 'Post'
        Uri = "$($ApiBaseUrl.TrimEnd('/'))/$($Path.TrimStart('/'))"
        ContentType = 'application/json'
        Body = ($Body | ConvertTo-Json -Depth 10)
    }
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $parameters.Headers = @{ Authorization = "Bearer $AccessToken" }
    }

    Invoke-RestMethod @parameters
}

function Invoke-ApiGet {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$AccessToken
    )

    Invoke-RestMethod `
        -Method Get `
        -Uri "$($ApiBaseUrl.TrimEnd('/'))/$($Path.TrimStart('/'))" `
        -Headers @{ Authorization = "Bearer $AccessToken" }
}

Write-Host '[1/7] 네 플레이어 계정을 등록합니다.' -ForegroundColor Cyan
$players = @()
for ($index = 1; $index -le 4; $index++) {
    $authentication = Invoke-ApiPost -Path 'api/auth/register' -Body @{
        loginId = "demo_${suffix}_$index"
        password = $demoPassword
        nickname = "Demo${suffix}$index"
    }
    $players += [pscustomobject]@{
        PlayerId = [Guid]$authentication.playerId
        AccessToken = [string]$authentication.accessToken
    }
}

Write-Host '[2/7] 첫 플레이어가 파티를 만들고 나머지 세 플레이어가 직접 가입합니다.' -ForegroundColor Cyan
$leader = $players[0]
$party = Invoke-ApiPost -Path 'api/parties' -AccessToken $leader.AccessToken -Body @{
    requestId = [Guid]::NewGuid()
    leaderPlayerId = $leader.PlayerId
}
for ($index = 1; $index -lt $players.Count; $index++) {
    $member = $players[$index]
    $party = Invoke-ApiPost -Path "api/parties/$($party.partyId)/members" -AccessToken $member.AccessToken -Body @{
        requestId = [Guid]::NewGuid()
        playerId = $member.PlayerId
    }
}

Write-Host '[3/7] 4인 파티를 한 티켓으로 매칭해 게임 방을 만듭니다.' -ForegroundColor Cyan
$matchmaking = Invoke-ApiPost `
    -Path "api/matchmaking/queues/$queueKey/parties/$($party.partyId)" `
    -AccessToken $leader.AccessToken `
    -Body @{ requestId = [Guid]::NewGuid() }
if ($null -eq $matchmaking.match -or $null -eq $matchmaking.match.roomId) {
    throw '4인 파티 매칭 응답에 roomId가 없습니다.'
}
$roomId = [Guid]$matchmaking.match.roomId

Write-Host '[4/7] 네 플레이어가 각자의 JWT로 방에 연결합니다.' -ForegroundColor Cyan
$connections = @()
foreach ($player in $players) {
    $connection = Invoke-ApiPost -Path "api/game-rooms/$roomId/connect" -AccessToken $player.AccessToken -Body @{
        requestId = [Guid]::NewGuid()
    }
    $connections += [pscustomobject]@{
        Player = $player
        ConnectionId = [Guid]$connection.connectionId
        Generation = [long]$connection.generation
    }
}

Write-Host '[5/7] 리더의 현재 연결 자격으로 전투를 시작하고 첫 기본 공격을 실행합니다.' -ForegroundColor Cyan
$leaderConnection = $connections[0]
$started = Invoke-ApiPost -Path "api/game-rooms/$roomId/start-combat" -AccessToken $leader.AccessToken -Body @{
    requestId = [Guid]::NewGuid()
    connectionId = $leaderConnection.ConnectionId
    generation = $leaderConnection.Generation
}
$attack = Invoke-ApiPost -Path "api/game-rooms/$roomId/basic-attack" -AccessToken $leader.AccessToken -Body @{
    requestId = [Guid]::NewGuid()
    connectionId = $leaderConnection.ConnectionId
    generation = $leaderConnection.Generation
    sequence = 1
    knownStateVersion = [long]$started.room.stateVersion
}

Write-Host '[6/7] 관리자 계정으로 로그인해 리더에게 멱등 보상을 지급합니다.' -ForegroundColor Cyan
$administrator = Invoke-ApiPost -Path 'api/auth/login' -Body @{
    loginId = $AdministratorLoginId
    password = $AdministratorPassword
}
$rewardRequestId = [Guid]::NewGuid()
$reward = Invoke-ApiPost -Path "api/players/$($leader.PlayerId)/rewards" -AccessToken $administrator.accessToken -Body @{
    requestId = $rewardRequestId
    goldAmount = 250
    itemId = 9001
    itemQuantity = 1
    reason = 'week-09-portfolio-demo'
}

Write-Host '[7/7] 관리자 조회 API에서 플레이어와 방금 지급한 보상 이력을 확인합니다.' -ForegroundColor Cyan
$lookup = Invoke-ApiGet `
    -Path "api/admin/players/lookup?query=$($leader.PlayerId)" `
    -AccessToken $administrator.accessToken
$history = @(Invoke-ApiGet `
    -Path "api/admin/players/$($leader.PlayerId)/reward-history?limit=5" `
    -AccessToken $administrator.accessToken)
if (-not ($history | Where-Object { $_.requestId -eq $rewardRequestId })) {
    throw '관리자 보상 이력에서 방금 사용한 requestId를 찾지 못했습니다.'
}

# 토큰과 비밀번호를 제외한 재현 증거만 출력합니다.
[pscustomobject]@{
    Players = $players.Count
    PartyId = [Guid]$party.partyId
    RoomId = $roomId
    RoomLifecycle = $attack.room.lifecycle
    StateVersion = [long]$attack.room.stateVersion
    RewardRequestId = [Guid]$reward.requestId
    RewardGold = [long]$reward.goldAmount
    AdministratorLookupPlayerId = [Guid]$lookup.playerId
    VerifiedRewardHistoryRows = $history.Count
}
