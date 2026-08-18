param(
    [string]$SourceSaveName = "COOP_LAN_BASE",
    [string]$RelayUrl = "https://coi-coop-relay.onrender.com",
    [ValidateRange(1024, 65532)]
    [int]$Port = 27015,
    [switch]$SelfTestHelpers
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

function Resolve-PreparedHostSave([string]$Value) {
    $saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
    if (-not (Test-Path -LiteralPath $saveRoot -PathType Container)) {
        throw "Captain of Industry save directory was not found: $saveRoot"
    }

    $allSaves = @(Get-ChildItem -LiteralPath $saveRoot -File -Recurse -Filter "*.save" | Sort-Object LastWriteTime -Descending)
    $source = $null

    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        $source = Get-Item -LiteralPath $Value
    }
    else {
        $leaf = $Value
        if (-not $leaf.EndsWith(".save", [StringComparison]::OrdinalIgnoreCase)) { $leaf += ".save" }
        $exact = @($allSaves | Where-Object { [string]::Equals($_.Name, $leaf, [StringComparison]::OrdinalIgnoreCase) })
        if ($exact.Count -eq 1) {
            $source = $exact[0]
        }
        elseif ($exact.Count -gt 1) {
            throw "More than one source save named '$leaf' exists; pass an exact .save path."
        }
        else {
            $needle = [IO.Path]::GetFileNameWithoutExtension($Value)
            $partial = @($allSaves | Where-Object { $_.BaseName.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.BaseName -notlike "*_HOST" -and $_.BaseName -notlike "*_CLIENT" })
            if ($partial.Count -ne 1) {
                throw "Could not resolve the prepared host baseline for '$Value'. Pass an exact source .save path."
            }
            $source = $partial[0]
        }
    }

    $hostPath = Join-Path $source.DirectoryName ($source.BaseName + "_HOST.save")
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) {
        throw "Prepared HOST save was not found after split-save preparation: $hostPath"
    }
    return (Resolve-Path -LiteralPath $hostPath).Path
}

function Get-CoiProcesses {
    return @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like "Captain*Industry*" }
    )
}

function Wait-CoiProcessExit([int]$ProcessId, [int]$TimeoutMs) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        while ($true) {
            $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $process) { return $true }
            if ($timer.ElapsedMilliseconds -ge $TimeoutMs) { return $false }
            Start-Sleep -Milliseconds 200
        }
    }
    finally {
        $timer.Stop()
    }
}

function Get-ProcessDetails([int]$ProcessId) {
    try {
        $row = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $ProcessId) -ErrorAction Stop
        if ($row) {
            return ("PID {0}, parent PID {1}, path '{2}'" -f $row.ProcessId, $row.ParentProcessId, $row.ExecutablePath)
        }
    }
    catch { }
    return ("PID {0}" -f $ProcessId)
}

function Force-KillSingleProcess([int]$ProcessId) {
    $stdoutPath = Join-Path $env:TEMP ("coi-coop-taskkill-{0}-{1}.out" -f $ProcessId, [Guid]::NewGuid().ToString("N"))
    $stderrPath = Join-Path $env:TEMP ("coi-coop-taskkill-{0}-{1}.err" -f $ProcessId, [Guid]::NewGuid().ToString("N"))
    try {
        $killer = Start-Process -FilePath "taskkill.exe" -ArgumentList @("/PID", [string]$ProcessId, "/F") -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $stdout = if (Test-Path $stdoutPath) { (Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue) } else { "" }
        $stderr = if (Test-Path $stderrPath) { (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue) } else { "" }
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr.Trim() -ForegroundColor Yellow }
        return $killer.ExitCode
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
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
        $pidToStop = [int]$process.Id
        Write-Host ("Stopping COI PID {0}..." -f $pidToStop) -ForegroundColor Yellow
        Stop-Process -Id $pidToStop -Force -ErrorAction SilentlyContinue

        if (-not (Wait-CoiProcessExit -ProcessId $pidToStop -TimeoutMs 5000)) {
            Write-Host "COI did not exit promptly; using exact-PID taskkill fallback..." -ForegroundColor Yellow
            $exitCode = Force-KillSingleProcess -ProcessId $pidToStop
            if ($exitCode -ne 0) {
                Write-Host ("taskkill exit code: {0}" -f $exitCode) -ForegroundColor Yellow
            }
            [void](Wait-CoiProcessExit -ProcessId $pidToStop -TimeoutMs 5000)
        }
    }

    $remaining = @(Get-CoiProcesses)
    if ($remaining.Count -gt 0) {
        Write-Host ""
        Write-Host "The following COI process(es) survived Stop-Process and exact-PID taskkill:" -ForegroundColor Red
        foreach ($process in $remaining) {
            Write-Host ("  " + (Get-ProcessDetails -ProcessId $process.Id)) -ForegroundColor Red
        }
        throw "Windows is keeping a stale Captain of Industry process alive. End that exact PID in Task Manager (Details tab) or reboot once, then retry."
    }

    Write-Host "Previous COI processes closed." -ForegroundColor Green
    Write-Host ""
}

if ($SelfTestHelpers) {
    $waitResult = Wait-CoiProcessExit -ProcessId $PID -TimeoutMs 50
    if ($waitResult) {
        throw "Wait-CoiProcessExit self-test unexpectedly reported the current PowerShell process as exited."
    }
    $normalized = Normalize-SessionCode "abcd-2345"
    if ($normalized -ne "ABCD-2345") {
        throw "Normalize-SessionCode self-test failed: $normalized"
    }
    $relay = Normalize-RelayBase "https://coi-coop-relay.onrender.com/"
    if ($relay -ne "https://coi-coop-relay.onrender.com") {
        throw "Normalize-RelayBase self-test failed: $relay"
    }
    Write-Host "HOST+BOT HELPER SELF-TEST PASSED" -ForegroundColor Green
    exit 0
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

    $preparedHostSave = Resolve-PreparedHostSave $SourceSaveName
    $baselineSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $preparedHostSave).Hash.ToUpperInvariant()
    Write-Host "Protocol-v5 baseline: $baselineSha256" -ForegroundColor Green

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
    $env:COI_COOP_BASELINE_SHA256 = $baselineSha256

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
    $env:COI_COOP_BASELINE_SHA256 = $baselineSha256
    Remove-Item Env:COI_COOP_TEST_PEER_ROLE -ErrorAction SilentlyContinue

    Write-Host "Launching the ONLY real Captain of Industry process as HOST..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "launch-coop.ps1") -Mode Host -Port $Port -Replay

    Write-Host ""
    Write-Host "HOST + BOT TEST READY" -ForegroundColor Green
    Write-Host "Load: $([IO.Path]::GetFileNameWithoutExtension($preparedHostSave))" -ForegroundColor Yellow
    Write-Host "Baseline: $baselineSha256" -ForegroundColor Green
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
