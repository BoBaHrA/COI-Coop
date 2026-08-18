$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactDir = Join-Path $repoRoot "artifacts\headless-peer"
$logDir = Join-Path $env:APPDATA "Captain of Industry\Logs"

if (-not (Test-Path $artifactDir)) {
    throw "Headless peer artifact directory was not found: $artifactDir"
}
if (-not (Test-Path $logDir)) {
    throw "Captain of Industry log directory was not found: $logDir"
}

$gameLog = Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$botLog = Get-ChildItem $artifactDir -File -Filter "test-peer-*.log" |
    Where-Object { $_.Name -notlike "*.err.log" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$botErr = Get-ChildItem $artifactDir -File -Filter "test-peer-*.err.log" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $gameLog) { throw "No Captain of Industry log was found." }
if (-not $botLog) { throw "No headless peer log was found." }

$gameCoop = @(Select-String -Path $gameLog.FullName -SimpleMatch "COI-Coop:" | ForEach-Object { $_.Line })
$botLines = @(Get-Content -LiteralPath $botLog.FullName)

function Count-Matches([string[]]$InputLines, [string]$Needle) {
    return @($InputLines | Where-Object { $_ -like ("*" + $Needle + "*") }).Count
}

function Count-RegexMatches([string[]]$InputLines, [string]$Pattern) {
    return @($InputLines | Where-Object { $_ -match $Pattern }).Count
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outPath = Join-Path $artifactDir ("host-bot-diagnostics-" + $stamp + ".txt")
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("COI-Coop host + headless client diagnostics")
$lines.Add("Created: " + (Get-Date -Format o))
$lines.Add("")
$lines.Add("===== SUMMARY =====")
$lines.Add("Gameplay WELCOME: " + (Count-Matches $botLines "gameplay WELCOME accepted"))
$lines.Add("Preview  WELCOME: " + (Count-Matches $botLines "preview preview WELCOME accepted"))
$lines.Add("Path     WELCOME: " + (Count-Matches $botLines "path preview WELCOME accepted"))
$lines.Add("Blueprint WELCOME: " + (Count-Matches $botLines "blueprint preview WELCOME accepted"))
$lines.Add("Preview payload log samples lane1: " + (Count-RegexMatches $botLines '^TEST-PEER preview PREVIEW rev='))
$lines.Add("Path payload log samples lane2: " + (Count-RegexMatches $botLines '^TEST-PEER path PREVIEW rev='))
$lines.Add("Blueprint payload log samples lane3: " + (Count-RegexMatches $botLines '^TEST-PEER blueprint PREVIEW rev='))
$lines.Add("Game placement TX starts: " + (Count-Matches $gameCoop "PREVIEW GHOST TX START"))
$lines.Add("Game placement-multi TX starts: " + (Count-Matches $gameCoop "PREVIEW MULTI GHOST TX START"))
$lines.Add("Game path TX starts: " + (Count-Matches $gameCoop "PATH GHOST TX START"))
$lines.Add("Game blueprint TX starts: " + (Count-Matches $gameCoop "BLUEPRINT GHOST TX START"))
$lines.Add("Baseline mismatches: " + (Count-Matches $gameCoop "BASELINE MISMATCH"))
$lines.Add("Relay disconnects: " + (Count-Matches $gameCoop "relay disconnected"))
$lines.Add("REPLAY HALT: " + (Count-Matches $gameCoop "REPLAY HALT"))
$lines.Add("FRAME BLOCKED: " + (Count-Matches $gameCoop "FRAME BLOCKED"))
$lines.Add("NETWORK RX FAIL: " + (Count-Matches $gameCoop "NETWORK RX FAIL"))
$lines.Add("SERIALIZE FAIL: " + (Count-Matches $gameCoop "SERIALIZE FAIL"))
$lines.Add("STATE DESYNC SUSPECTED: " + (Count-Matches $gameCoop "STATE DESYNC SUSPECTED"))
$lines.Add("")
$lines.Add("===== GAME LOG: " + $gameLog.Name + " =====")
if ($gameCoop.Count -eq 0) {
    $lines.Add("No COI-Coop lines found.")
} else {
    foreach ($line in $gameCoop) { $lines.Add($line) }
}

$lines.Add("")
$lines.Add("===== TEST PEER LOG: " + $botLog.Name + " =====")
foreach ($line in $botLines) { $lines.Add($line) }

if ($botErr -and $botErr.Length -gt 0) {
    $lines.Add("")
    $lines.Add("===== TEST PEER STDERR: " + $botErr.Name + " =====")
    foreach ($line in Get-Content -LiteralPath $botErr.FullName) { $lines.Add($line) }
}

$pidPath = Join-Path $artifactDir "current.pid"
if (Test-Path $pidPath) {
    $pidText = (Get-Content -LiteralPath $pidPath -Raw).Trim()
    $peerPid = 0
    if ([int]::TryParse($pidText, [ref]$peerPid)) {
        $peer = Get-Process -Id $peerPid -ErrorAction SilentlyContinue
        if ($peer) {
            try { Stop-Process -Id $peerPid -Force -ErrorAction Stop } catch { }
            $lines.Add("")
            $lines.Add("Stopped headless test peer PID " + $peerPid + ".")
        }
    }
    Remove-Item $pidPath -Force -ErrorAction SilentlyContinue
}

Set-Content -LiteralPath $outPath -Value $lines -Encoding UTF8

Write-Host ""
Write-Host "HOST + BOT DIAGNOSTICS READY" -ForegroundColor Green
Write-Host "File: $outPath"
Write-Host ""
Write-Host "Paste the file contents into the chat."
