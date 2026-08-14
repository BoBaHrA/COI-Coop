param(
    [string]$SourceSaveName = "COOP_REPLAY_TEST"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
if (-not (Test-Path $saveRoot)) {
    throw "Captain of Industry save directory was not found: $saveRoot"
}

$leaf = $SourceSaveName
if (-not $leaf.EndsWith(".save", [StringComparison]::OrdinalIgnoreCase)) {
    $leaf += ".save"
}

$matches = @(Get-ChildItem $saveRoot -File -Recurse -Filter $leaf)
if ($matches.Count -eq 0) {
    throw "Save '$leaf' was not found under: $saveRoot"
}

if ($matches.Count -gt 1) {
    Write-Host "More than one save named '$leaf' was found:" -ForegroundColor Yellow
    $matches | ForEach-Object { Write-Host "  $($_.FullName)" }
    throw "Please rename the intended source save so its name is unique, then run this helper again."
}

$source = $matches[0]
$directory = $source.DirectoryName
$baseName = [IO.Path]::GetFileNameWithoutExtension($source.Name)
$hostPath = Join-Path $directory ($baseName + "_HOST.save")
$clientPath = Join-Path $directory ($baseName + "_CLIENT.save")

Write-Host "Source: $($source.FullName)"
Write-Host "Creating two independent byte-identical starting saves..."

Copy-Item -LiteralPath $source.FullName -Destination $hostPath -Force
Copy-Item -LiteralPath $source.FullName -Destination $clientPath -Force

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source.FullName).Hash
$hostHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $hostPath).Hash
$clientHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $clientPath).Hash

if ($sourceHash -ne $hostHash -or $sourceHash -ne $clientHash) {
    throw "Replay save copies were created but their SHA-256 hashes do not match the source."
}

Write-Host ""
Write-Host "REPLAY SAVES READY" -ForegroundColor Green
Write-Host "Host:   $hostPath"
Write-Host "Client: $clientPath"
Write-Host "SHA256: $sourceHash"
Write-Host ""
Write-Host "Load '${baseName}_HOST' in the HOST game window."
Write-Host "Load '${baseName}_CLIENT' in the CLIENT game window."
Write-Host "Do not save either test world after the replay experiment."
