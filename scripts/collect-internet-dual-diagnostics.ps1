$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$logDir = Join-Path $env:APPDATA "Captain of Industry\Logs"
if (-not (Test-Path $logDir)) {
    throw "Captain of Industry log directory was not found: $logDir"
}

$logs = @(
    Get-ChildItem $logDir -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 2
)

if ($logs.Count -lt 2) {
    throw "Two Captain of Industry logs are required. Run the dual test first and close both game processes."
}

$outDir = Join-Path $repoRoot "artifacts\internet-dual"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outPath = Join-Path $outDir ("internet-dual-" + $stamp + ".txt")

$markers = @(
    "INTERNET RELAY enabled",
    "WSS connected",
    "session connected",
    "gameplay READY",
    "FRAME WAITING",
    "FRAME APPLY",
    "FRAME BLOCKED",
    "REPLAY WAITING",
    "REPLAY BLOCKED",
    "REPLAY SUBMIT",
    "REPLAY APPLIED",
    "STATE PROBE MATCH",
    "STATE PROBE MISMATCH",
    "REPLAY HALT",
    "NETWORK RX FAIL",
    "SERIALIZE FAIL"
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("COI-Coop single-PC internet dual diagnostics")
$lines.Add("Created: " + (Get-Date -Format o))
$lines.Add("")

for ($i = 0; $i -lt $logs.Count; $i++) {
    $log = $logs[$i]
    $coopLines = @(
        Select-String -Path $log.FullName -SimpleMatch "COI-Coop:" |
            ForEach-Object { $_.Line }
    )

    $lines.Add("===== LOG $($i + 1)/2: $($log.Name) =====")
    $lines.Add("Path: " + $log.FullName)
    $lines.Add("LastWriteTime: " + $log.LastWriteTime.ToString("o"))
    $lines.Add("")
    $lines.Add("Marker counts:")

    foreach ($marker in $markers) {
        $count = @($coopLines | Where-Object { $_.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
        $lines.Add(("  {0}: {1}" -f $marker, $count))
    }

    $lines.Add("")
    $lines.Add("COI-Coop lines:")
    if ($coopLines.Count -eq 0) {
        $lines.Add("  No COI-Coop lines found.")
    }
    else {
        foreach ($line in $coopLines) {
            $lines.Add($line)
        }
    }
    $lines.Add("")
}

Set-Content -LiteralPath $outPath -Value $lines -Encoding UTF8

Write-Host ""
Write-Host "INTERNET DUAL DIAGNOSTICS READY" -ForegroundColor Green
Write-Host "File: $outPath"
Write-Host ""
Write-Host "Paste this file or its contents into the chat."
