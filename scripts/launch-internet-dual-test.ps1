param(
    [string]$SourceSaveName = "COOP_LAN_BASE",
    [string]$RelayUrl = "https://coi-coop-relay.onrender.com",
    [ValidateRange(1024, 65528)]
    [int]$HostPort = 27015,
    [ValidateRange(1024, 65528)]
    [int]$ClientPort = 28015
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-RelayBase([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Relay URL is required."
    }

    $text = $Value.Trim().TrimEnd('/')
    $uri = $null
    $valid = [Uri]::TryCreate($text, [UriKind]::Absolute, [ref]$uri)
    if (-not $valid) {
        throw "Invalid relay URL: $Value"
    }
    if ($uri.Scheme -ne "https" -and $uri.Scheme -ne "http") {
        throw "Relay URL must start with https:// (or http:// for local development)."
    }
    return $text
}

function Normalize-SessionCode([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $raw = ($Value.ToUpperInvariant() -replace '[^A-Z0-9]', '')
    if ($raw.Length -ne 8) { return $null }
    return $raw.Substring(0, 4) + "-" + $raw.Substring(4, 4)
}

function To-WebSocketUrl([string]$BaseUrl) {
    $uri = [Uri]$BaseUrl
    $scheme = if ($uri.Scheme -eq "https") { "wss" } else { "ws" }
    return "${scheme}://$($uri.Authority)/relay"
}

function Assert-NoCoiProcesses {
    $running = @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like "Captain*Industry*" }
    )
    if ($running.Count -gt 0) {
        Write-Host "Captain of Industry is already running:" -ForegroundColor Yellow
        $running | ForEach-Object { Write-Host ("  PID {0}  {1}" -f $_.Id, $_.ProcessName) }
        throw "Close all Captain of Industry windows before starting a fresh dual test."
    }
}

try {
    if ($HostPort -eq $ClientPort -or [Math]::Abs($HostPort - $ClientPort) -lt 4) {
        throw "HostPort and ClientPort must use separate four-port ranges."
    }

    Assert-NoCoiProcesses

    Write-Host "=== COI-Coop SINGLE-PC INTERNET DUAL TEST ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Refreshing byte-identical test saves from '$SourceSaveName'..."
    # This is another PowerShell script, not an external process. With StrictMode,
    # $LASTEXITCODE may be unset here. Any preparation failure is already a
    # terminating exception because both scripts use ErrorActionPreference=Stop.
    & (Join-Path $PSScriptRoot "prepare-replay-saves.ps1") -SourceSaveName $SourceSaveName

    $baseUrl = Normalize-RelayBase $RelayUrl
    $wsUrl = To-WebSocketUrl $baseUrl

    Write-Host ""
    Write-Host "Creating a real public-relay session..."
    $hostResponse = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $code = Normalize-SessionCode ([string]$hostResponse.code)
    $hostToken = [string]$hostResponse.hostToken
    if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($hostToken)) {
        throw "Relay returned an invalid host session response."
    }

    $clientResponse = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $clientToken = [string]$clientResponse.clientToken
    if ([string]::IsNullOrWhiteSpace($clientToken)) {
        throw "Relay returned an invalid client session response."
    }

    Write-Host "Session: $code" -ForegroundColor Green
    Write-Host "Relay:   $baseUrl"
    Write-Host "Host local lanes:   $HostPort-$($HostPort + 3)"
    Write-Host "Client local lanes: $ClientPort-$($ClientPort + 3)"
    Write-Host ""

    $launcher = Join-Path $PSScriptRoot "launch-coop.ps1"

    $env:COI_COOP_LAN = "0"
    $env:COI_COOP_RELAY = "1"
    $env:COI_COOP_RELAY_WS = $wsUrl
    $env:COI_COOP_SESSION_CODE = $code

    Write-Host "Launching HOST process first..." -ForegroundColor Cyan
    $env:COI_COOP_SESSION_TOKEN = $hostToken
    & $launcher -Mode Host -Port $HostPort -Replay

    Start-Sleep -Seconds 2

    Write-Host ""
    Write-Host "Launching CLIENT process second..." -ForegroundColor Cyan
    $env:COI_COOP_SESSION_TOKEN = $clientToken
    & $launcher -Mode Client -Port $ClientPort -Replay

    Write-Host ""
    Write-Host "DUAL INTERNET TEST STARTED" -ForegroundColor Green
    Write-Host ""
    Write-Host "FIRST game process/window = HOST" -ForegroundColor Yellow
    Write-Host "  Load: ${SourceSaveName}_HOST"
    Write-Host "SECOND game process/window = CLIENT" -ForegroundColor Yellow
    Write-Host "  Load: ${SourceSaveName}_CLIENT"
    Write-Host ""
    Write-Host "Both saves were refreshed from the same source immediately before launch."
    Write-Host "Do not save either test copy. If either process exits, restart BOTH with this script."
    Write-Host "Reconnect/catch-up is intentionally NOT supported yet."
    Write-Host ""
    Write-Host "After reproducing a problem, close both games and run:" -ForegroundColor Cyan
    Write-Host "  scripts\collect-internet-dual-diagnostics.bat"
}
catch {
    Write-Host ""
    Write-Host "DUAL INTERNET TEST LAUNCH FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    exit 1
}
