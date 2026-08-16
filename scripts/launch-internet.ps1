param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Host", "Client")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$RelayUrl,

    [string]$SessionCode,

    [string]$CoiRoot = $env:COI_ROOT,

    [ValidateRange(1024, 65535)]
    [int]$Port = 27015
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-RelayBase([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Relay URL is required."
    }

    $text = $Value.Trim().TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($text, [UriKind]::Absolute, [ref]$uri)) {
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

$baseUrl = Normalize-RelayBase $RelayUrl
$wsUrl = To-WebSocketUrl $baseUrl
$token = $null
$code = $null
$expiresAt = $null

Write-Host "=== COI-Coop INTERNET $Mode ==="
Write-Host "Relay: $baseUrl"
Write-Host ""

if ($Mode -eq "Host") {
    Write-Host "Creating co-op session..."
    $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60

    $code = Normalize-SessionCode ([string]$response.code)
    $token = [string]$response.hostToken
    $expiresAt = [string]$response.expiresAt

    if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($token)) {
        throw "Relay returned an invalid host session response."
    }

    Write-Host ""
    Write-Host "=========================================" -ForegroundColor Cyan
    Write-Host "  SESSION CODE:  $code" -ForegroundColor Green
    Write-Host "=========================================" -ForegroundColor Cyan
    Write-Host "Send ONLY this code to your friend." -ForegroundColor Yellow
    if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
        Write-Host "Expires: $expiresAt"
    }
    Write-Host ""
}
else {
    if ([string]::IsNullOrWhiteSpace($SessionCode)) {
        $SessionCode = Read-Host "Session code"
    }
    $code = Normalize-SessionCode $SessionCode
    if ([string]::IsNullOrWhiteSpace($code)) {
        throw "Session code must contain exactly 8 letters/digits (for example ABCD-2345)."
    }

    Write-Host "Joining session $code..."
    $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60

    $token = [string]$response.clientToken
    $expiresAt = [string]$response.expiresAt
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Relay returned an invalid client session response."
    }

    Write-Host "Session accepted." -ForegroundColor Green
    if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
        Write-Host "Expires: $expiresAt"
    }
    Write-Host ""
}

# These values exist only in this launcher process and the Captain of Industry
# child process. Session tokens are never persisted to disk.
$env:COI_COOP_LAN = "0"
$env:COI_COOP_RELAY = "1"
$env:COI_COOP_RELAY_WS = $wsUrl
$env:COI_COOP_SESSION_CODE = $code
$env:COI_COOP_SESSION_TOKEN = $token

$launcher = Join-Path $PSScriptRoot "launch-coop.ps1"
& $launcher -Mode $Mode -CoiRoot $CoiRoot -Port $Port -Replay
exit $LASTEXITCODE
