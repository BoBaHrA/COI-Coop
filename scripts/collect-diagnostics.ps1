param(
    [switch]$CopyToClipboard
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$logDir = Join-Path $env:APPDATA "Captain of Industry\Logs"
if (-not (Test-Path $logDir)) {
    throw "Captain of Industry log directory was not found: $logDir"
}

$latest = Get-ChildItem $logDir -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latest) {
    throw "No Captain of Industry log files were found in: $logDir"
}

$matches = Select-String -Path $latest.FullName -SimpleMatch "COI-Coop:" |
    ForEach-Object { $_.Line }

Write-Host "Log: $($latest.FullName)"
Write-Host ""

if (-not $matches) {
    Write-Host "No COI-Coop lines found in the latest log."
    exit 2
}

$text = $matches -join [Environment]::NewLine
Write-Output $text

if ($CopyToClipboard) {
    Set-Clipboard -Value $text
    Write-Host ""
    Write-Host "COI-Coop diagnostics copied to clipboard."
}
