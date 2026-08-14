param(
    [switch]$CopyToClipboard,

    [ValidateRange(1, 10)]
    [int]$LatestCount = 1
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

if ($logs.Count -eq 0) {
    throw "No Captain of Industry log files were found in: $logDir"
}

$sections = New-Object System.Collections.Generic.List[string]
$foundAny = $false

for ($i = 0; $i -lt $logs.Count; $i++) {
    $log = $logs[$i]
    $matches = @(
        Select-String -Path $log.FullName -SimpleMatch "COI-Coop:" |
            ForEach-Object { $_.Line }
    )

    $header = "===== LOG $($i + 1)/$($logs.Count): $($log.Name) ====="
    $sections.Add($header)

    if ($matches.Count -eq 0) {
        $sections.Add("No COI-Coop lines found in this log.")
    }
    else {
        $foundAny = $true
        foreach ($line in $matches) {
            $sections.Add($line)
        }
    }

    if ($i -lt $logs.Count - 1) {
        $sections.Add("")
    }
}

$text = $sections -join [Environment]::NewLine
Write-Output $text

if (-not $foundAny) {
    exit 2
}

if ($CopyToClipboard) {
    Set-Clipboard -Value $text
    Write-Host ""
    Write-Host "COI-Coop diagnostics copied to clipboard."
}
