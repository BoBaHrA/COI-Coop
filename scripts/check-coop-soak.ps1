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

# A peer that is intentionally closed after a completed test causes the surviving
# process to fail-closed with this exact marker. That is useful runtime behavior but
# should not invalidate an otherwise matched soak run. We classify it separately and
# only downgrade it to an expected teardown warning when both peers processed the same
# non-zero number of authoritative replay commands and there are no other hard errors.
$teardownDisconnectPattern = "REPLAY HALT - network session disconnected after authority replay started"

$hardFatalPatterns = @(
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

$totalHardFatal = 0
$totalTeardownDisconnect = 0
$totalReplayApplied = 0
$totalProbeMatches = 0
$totalPhaseSkips = 0
$allReady = $true
$replayAppliedCounts = New-Object System.Collections.Generic.List[int]

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
    $replayAppliedCounts.Add($replayApplied)
    $totalReplayApplied += $replayApplied
    $totalProbeMatches += $probeMatches
    $totalPhaseSkips += $phaseSkips

    $hardFatalLines = New-Object System.Collections.Generic.List[string]
    $teardownLines = New-Object System.Collections.Generic.List[string]

    foreach ($line in $coopLines) {
        if ($line.IndexOf($teardownDisconnectPattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $teardownLines.Add($line)
            continue
        }

        foreach ($pattern in $hardFatalPatterns) {
            if ($line.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $hardFatalLines.Add($line)
                break
            }
        }
    }

    $totalHardFatal += $hardFatalLines.Count
    $totalTeardownDisconnect += $teardownLines.Count

    $report.Add("[$($i + 1)] $($log.Name)")
    $report.Add("    gameplay READY: " + ($(if ($readyCount -gt 0) { "YES" } else { "NO" })))
    $report.Add("    REPLAY APPLIED: $replayApplied")
    $report.Add("    STATE PROBE MATCH: $probeMatches")
    $report.Add("    STATE PROBE PHASE SKIP: $phaseSkips")
    $report.Add("    hard fatal/error markers: $($hardFatalLines.Count)")
    $report.Add("    teardown disconnect markers: $($teardownLines.Count)")

    if ($hardFatalLines.Count -gt 0) {
        $shown = [Math]::Min(12, $hardFatalLines.Count)
        for ($j = 0; $j -lt $shown; $j++) {
            $report.Add("      ! " + $hardFatalLines[$j])
        }
        if ($hardFatalLines.Count -gt $shown) {
            $report.Add("      ... $($hardFatalLines.Count - $shown) more")
        }
    }

    if ($teardownLines.Count -gt 0) {
        $shown = [Math]::Min(4, $teardownLines.Count)
        for ($j = 0; $j -lt $shown; $j++) {
            $report.Add("      ~ " + $teardownLines[$j])
        }
    }

    $report.Add("")
}

$matchedReplayCounts = $false
if ($replayAppliedCounts.Count -eq $logs.Count -and $replayAppliedCounts.Count -gt 0) {
    $firstReplayCount = $replayAppliedCounts[0]
    $matchedReplayCounts = $firstReplayCount -gt 0
    foreach ($count in $replayAppliedCounts) {
        if ($count -ne $firstReplayCount) {
            $matchedReplayCounts = $false
            break
        }
    }
}

$expectedTeardownOnly =
    $allReady -and
    $totalHardFatal -eq 0 -and
    $totalTeardownDisconnect -gt 0 -and
    $matchedReplayCounts

$report.Add("Totals:")
$report.Add("  REPLAY APPLIED: $totalReplayApplied")
$report.Add("  STATE PROBE MATCH: $totalProbeMatches")
$report.Add("  STATE PROBE PHASE SKIP: $totalPhaseSkips")
$report.Add("  hard fatal/error markers: $totalHardFatal")
$report.Add("  teardown disconnect markers: $totalTeardownDisconnect")
$report.Add("")

if (-not $allReady) {
    $verdict = "INCOMPLETE - at least one log never reached gameplay READY"
    $exitCode = 2
}
elseif ($totalHardFatal -gt 0) {
    $verdict = "FAIL - hard regression markers found; inspect diagnostics before merging"
    $exitCode = 1
}
elseif ($totalTeardownDisconnect -gt 0 -and -not $expectedTeardownOnly) {
    $verdict = "FAIL - peer disconnect could not be proven to be matched end-of-test teardown"
    $exitCode = 1
}
elseif ($expectedTeardownOnly) {
    $verdict = "PASS - peers processed matching replay counts; only end-of-test teardown disconnect was observed"
    $exitCode = 0
}
else {
    $verdict = "PASS - no known fatal/error markers found in the two latest logs"
    $exitCode = 0
}

$report.Add("VERDICT: $verdict")
$report.Add("Note: PHASE SKIP is informational and is not treated as a failure by this checker.")
if ($expectedTeardownOnly) {
    $report.Add("Note: teardown PASS assumes one peer was intentionally closed after the test. If the disconnect was unexpected during play, treat it as a real failure.")
}
$report.Add("A PASS is a regression gate, not proof that every simulation subsystem is deterministic.")

$text = $report -join [Environment]::NewLine
Write-Output $text

if ($CopyToClipboard) {
    Set-Clipboard -Value $text
    Write-Host ""
    Write-Host "Soak regression summary copied to clipboard."
}

exit $exitCode
