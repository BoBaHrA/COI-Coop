param(
    [string]$SourceSaveName = "COOP_LAN_BASE",
    [string]$RelayUrl = "https://coi-coop-relay.onrender.com",
    [ValidateRange(1024, 65532)]
    [int]$Port = 27015
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-RelayBase([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "Relay URL is required." }
    $text = $Value.Trim().TrimEnd('/')
    $uri = $null
    $valid = [Uri]::TryCreate($text, [UriKind]::Absolute, [ref]$uri)
    if (-not $valid) { throw "Invalid relay URL: $Value" }
    if ($uri.Scheme -ne "https" -and $uri.Scheme -ne "http") {
        throw "Relay URL must start with https:// (or http:// for local development)."
    }
    return $text
}

function To-WebSocketUrl([string]$BaseUrl) {
    $uri = [Uri]$BaseUrl
    $scheme = if ($uri.Scheme -eq "https") { "wss" } else { "ws" }
    return "${scheme}://$($uri.Authority)/relay"
}

function Normalize-SessionCode([string]$Value) {
    $text = if ($null -eq $Value) { "" } else { [string]$Value }
    $raw = ($text.ToUpperInvariant() -replace '[^A-Z0-9]', '')
    if ($raw.Length -ne 8) { return $null }
    return $raw.Substring(0, 4) + "-" + $raw.Substring(4, 4)
}

function Get-CoiProcesses {
    return @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like "Captain*Industry*" }
    )
}

function Ensure-NoCoiProcesses {
    $running = @(Get-CoiProcesses)
    if ($running.Count -eq 0) { return }

    Write-Host "Captain of Industry process(es) from a previous test are still running:" -ForegroundColor Yellow
    foreach ($process in $running) {
        $title = ""
        try { $title = [string]$process.MainWindowTitle } catch { }
        if ([string]::IsNullOrWhiteSpace($title)) { $title = "(no visible window title)" }
        Write-Host ("  PID {0}  {1}  {2}" -f $process.Id, $process.ProcessName, $title)
    }
    Write-Host ""
    Write-Host "A clean host+bot test must start with zero COI processes." -ForegroundColor Yellow
    $answer = Read-Host "Close ONLY these Captain of Industry processes now? [Y/N]"
    if ($answer -notmatch '^(?i:y|yes)$') {
        throw "Close all Captain of Industry processes and re-run the host+bot test."
    }

    foreach ($process in $running) {
        Write-Host ("Stopping COI PID {0}..." -f $process.Id) -ForegroundColor Yellow
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 750
    $remaining = @(Get-CoiProcesses)
    if ($remaining.Count -gt 0) {
        $ids = ($remaining | ForEach-Object { [string]$_.Id }) -join ", "
        throw "Some Captain of Industry processes are still running (PID: $ids). Close them manually and retry."
    }

    Write-Host "Previous COI processes closed." -ForegroundColor Green
    Write-Host ""
}

try {
    Ensure-NoCoiProcesses
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $artifactDir = Join-Path $repoRoot "artifacts\headless-peer"
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

    $oldPidPath = Join-Path $artifactDir "current.pid"
    if (Test-Path $oldPidPath) {
        $oldText = (Get-Content -LiteralPath $oldPidPath -Raw).Trim()
        $oldPid = 0
        if ([int]::TryParse($oldText, [ref]$oldPid)) {
            $oldProcess = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
            if ($oldProcess) {
                Write-Host "Stopping stale test peer PID $oldPid..." -ForegroundColor Yellow
                Stop-Process -Id $oldPid -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item $oldPidPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "=== COI-Coop INTERNET HOST + HEADLESS CLIENT ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Refreshing disposable test saves from '$SourceSaveName'..."
    & (Join-Path $PSScriptRoot "prepare-replay-saves.ps1") -SourceSaveName $SourceSaveName

    $project = Join-Path $repoRoot "tools\CoiCoop.TestPeer\CoiCoop.TestPeer.csproj"
    Write-Host ""
    Write-Host "Building headless test peer..."
    & dotnet build $project -c Release --nologo
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) { throw "Test peer build failed with exit code $buildExit." }

    $botDll = Join-Path $repoRoot "tools\CoiCoop.TestPeer\bin\Release\net8.0\CoiCoop.TestPeer.dll"
    if (-not (Test-Path -LiteralPath $botDll -PathType Leaf)) {
        throw "Built test peer DLL was not found: $botDll"
    }

    $baseUrl = Normalize-RelayBase $RelayUrl
    $wsUrl = To-WebSocketUrl $baseUrl

    Write-Host ""
    Write-Host "Creating public relay session..."
    $hostResponse = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $code = Normalize-SessionCode ([string]$hostResponse.code)
    $hostToken = [string]$hostResponse.hostToken
    if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($hostToken)) {
        throw "Relay returned an invalid host session response."
    }

    $clientResponse = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $clientToken = [string]$clientResponse.clientToken
    if ([string]::IsNullOrWhiteSpace($clientToken)) {
        throw "Relay returned an invalid test-client token."
    }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $botLog = Join-Path $artifactDir ("test-peer-" + $stamp + ".log")
    $botErr = Join-Path $artifactDir ("test-peer-" + $stamp + ".err.log")

    $env:COI_COOP_RELAY_WS = $wsUrl
    $env:COI_COOP_SESSION_CODE = $code
    $env:COI_COOP_SESSION_TOKEN = $clientToken
    $env:COI_COOP_TEST_PEER_ROLE = "client"

    $botProcess = Start-Process -FilePath "dotnet" -ArgumentList ('"' + $botDll + '"') -RedirectStandardOutput $botLog -RedirectStandardError $botErr -PassThru
    Set-Content -LiteralPath $oldPidPath -Value ([string]$botProcess.Id) -Encoding ASCII
    Start-Sleep -Seconds 1
    if ($botProcess.HasExited) {
        $errorText = if (Test-Path $botErr) { (Get-Content -LiteralPath $botErr -Raw) } else { "" }
        throw "Headless test peer exited immediately. $errorText"
    }

    Write-Host "Session: $code" -ForegroundColor Green
    Write-Host "Headless client PID: $($botProcess.Id)"
    Write-Host "Headless client log: $botLog"
    Write-Host ""

    $env:COI_COOP_LAN = "0"
    $env:COI_COOP_RELAY = "1"
    $env:COI_COOP_RELAY_WS = $wsUrl
    $env:COI_COOP_SESSION_CODE = $code
    $env:COI_COOP_SESSION_TOKEN = $hostToken
    Remove-Item Env:COI_COOP_TEST_PEER_ROLE -ErrorAction SilentlyContinue

    Write-Host "Launching the ONLY real Captain of Industry process as HOST..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "launch-coop.ps1") -Mode Host -Port $Port -Replay

    Write-Host ""
    Write-Host "HOST + BOT TEST READY" -ForegroundColor Green
    Write-Host "Load: ${SourceSaveName}_HOST" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "This harness tests gameplay command capture/serialization/authority/replay through the REAL Render relay."
    Write-Host "It does not validate a second simulation or remote placement rendering."
    Write-Host ""
    Write-Host "When finished, close COI and run:" -ForegroundColor Cyan
    Write-Host "  scripts\collect-host-bot-diagnostics.bat"
}
catch {
    Write-Host ""
    Write-Host "HOST + BOT TEST LAUNCH FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    exit 1
}
