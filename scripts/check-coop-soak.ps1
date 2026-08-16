param(
    [ValidateRange(1, 10)]
    [int]$LatestCount = 2,

    [switch]$CopyToClipboard
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$logDir = Join-Path $env:APPDATA "Captain of Industry\Logs"
if (-not (Test-Path $logDir)) {
    throw "Captain of Industry log directory was not found: $logDir"
}

$logs = @(
    Get-ChildItem $logDir -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $LatestCount
)

if ($logs.Count -lt 2) {
    throw "Need at least two recent Captain of Industry logs for a two-peer soak check. Found: $($logs.Count)"
}

$fatalPatterns = @(
    "REPLAY HALT",
    "NETWORK RX FAIL",
    "SERIALIZE FAIL",
    "STATE PROBE MISMATCH",
    "DESYNC WARNING",
    "PREVIEW GHOST RX FAIL",
    "PREVIEW GHOST ENCODE FAIL",
    "PREVIEW MULTI GHOST RX FAIL",
    "PREVIEW MULTI GHOST ENCODE FAIL",
    "PATH GHOST RX FAIL",
    "PATH GHOST ENCODE FAIL",
    "RAMP GHOST RX FAIL",
    "RAMP GHOST ENCODE FAIL",
    "REMOTE GHOST RENDER FAIL",
    "REMOTE PATH GHOST RENDER FAIL",
    "SANDBOX SYNC CAPTURE FAIL",
    "SANDBOX SYNC RX FAIL"
)

$report = New-Object System.Collections.Generic.List[string]
$report.Add("=== COI-Coop soak regression check ===")
$report.Add("Logs: $($logs.Count)")
$report.Add("")

$totalFatal = 0
$totalReplayApplied = 0
$totalProbeMatches = 0
$totalPhaseSkips = 0
$allReady = $true

for ($i = 0; $i -lt $logs.Count; $i++) {
    $log = $logs[$i]
    $coopLines = @(
        Select-String -Path $log.FullName -SimpleMatch "COI-Coop:" |
            ForEach-Object { $_.Line }
    )

    $readyCount = @($coopLines | Where-Object { $_ -like "*FRAME local gameplay READY*" }).Count
    $replayApplied = @($coopLines | Where-Object { $_ -like "*REPLAY APPLIED*" }).Count
    $probeMatches = @($coopLines | Where-Object { $_ -like "*STATE PROBE MATCH*" }).Count
    $phaseSkips = @($coopLines | Where-Object { $_ -like "*STATE PROBE PHASE SKIP*" }).Count

    if ($readyCount -eq 0) { $allReady = $false }
    $totalReplayApplied += $replayApplied
    $totalProbeMatches += $probeMatches
    $totalPhaseSkips += $phaseSkips

    $fatalLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in $coopLines) {
        foreach ($pattern in $fatalPatterns) {
            if ($line.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $fatalLines.Add($line)
                break
            }
        }
    }

    $totalFatal += $fatalLines.Count

    $report.Add("[$($i + 1)] $($log.Name)")
    $report.Add("    gameplay READY: " + ($(if ($readyCount -gt 0) { "YES" } else { "NO" })))
    $report.Add("    REPLAY APPLIED: $replayApplied")
    $report.Add("    STATE PROBE MATCH: $probeMatches")
    $report.Add("    STATE PROBE PHASE SKIP: $phaseSkips")
    $report.Add("    fatal/error markers: $($fatalLines.Count)")

    if ($fatalLines.Count -gt 0) {
        $shown = [Math]::Min(12, $fatalLines.Count)
        for ($j = 0; $j -lt $shown; $j++) {
            $report.Add("      ! " + $fatalLines[$j])
        }
        if ($fatalLines.Count -gt $shown) {
            $report.Add("      ... $($fatalLines.Count - $shown) more")
        }
    }

    $report.Add("")
}

$report.Add("Totals:")
$report.Add("  REPLAY APPLIED: $totalReplayApplied")
$report.Add("  STATE PROBE MATCH: $totalProbeMatches")
$report.Add("  STATE PROBE PHASE SKIP: $totalPhaseSkips")
$report.Add("  fatal/error markers: $totalFatal")
$report.Add("")

if (-not $allReady) {
    $verdict = "INCOMPLETE - at least one log never reached gameplay READY"
    $exitCode = 2
}
elseif ($totalFatal -gt 0) {
    $verdict = "FAIL - regression markers found; inspect diagnostics before merging"
    $exitCode = 1
}
else {
    $verdict = "PASS - no known fatal/error markers found in the two latest logs"
    $exitCode = 0
}

$report.Add("VERDICT: $verdict")
$report.Add("Note: PHASE SKIP is informational and is not treated as a failure by this checker.")
$report.Add("A PASS is a regression gate, not proof that every simulation subsystem is deterministic.")

$text = $report -join [Environment]::NewLine
Write-Output $text

if ($CopyToClipboard) {
    Set-Clipboard -Value $text
    Write-Host ""
    Write-Host "Soak regression summary copied to clipboard."
}

exit $exitCode
