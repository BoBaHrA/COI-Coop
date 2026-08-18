param(
    [string]$RelayUrl = "https://coi-coop-relay.onrender.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-RelayBase([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "Relay URL is required." }
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

$tempSource = Join-Path $env:TEMP ("coi-coop-public-snapshot-source-" + [Guid]::NewGuid().ToString("N") + ".save")
$tempDownload = Join-Path $env:TEMP ("coi-coop-public-snapshot-download-" + [Guid]::NewGuid().ToString("N") + ".save")

try {
    $baseUrl = Normalize-RelayBase $RelayUrl
    Write-Host "=== COI-Coop PUBLIC RELAY SNAPSHOT TEST ===" -ForegroundColor Cyan
    Write-Host "Relay: $baseUrl"

    $bytes = New-Object byte[] 65536
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [IO.File]::WriteAllBytes($tempSource, $bytes)
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $tempSource).Hash

    Write-Host "Creating real relay session..."
    $created = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $code = Normalize-SessionCode ([string]$created.code)
    $hostToken = [string]$created.hostToken
    if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($hostToken)) {
        throw "Relay returned an invalid create-session response."
    }

    Write-Host "Session: $code"
    Write-Host "Uploading 65536-byte synthetic snapshot..."
    $upload = Invoke-WebRequest `
        -Method Put `
        -Uri "$baseUrl/api/session/$code/snapshot" `
        -Headers @{ Authorization = "Bearer $hostToken"; "X-COI-Save-Name" = "PUBLIC_SMOKE_HOST.save" } `
        -ContentType "application/octet-stream" `
        -InFile $tempSource `
        -UseBasicParsing `
        -TimeoutSec 120
    $uploadMeta = $upload.Content | ConvertFrom-Json
    if (-not [string]::Equals([string]$uploadMeta.sha256, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Upload metadata SHA256 mismatch."
    }

    $joined = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60
    $clientToken = [string]$joined.clientToken
    if ([string]::IsNullOrWhiteSpace($clientToken)) {
        throw "Relay returned an invalid join response."
    }
    $snapshotProperty = $joined.PSObject.Properties["snapshot"]
    if ($null -eq $snapshotProperty -or $null -eq $snapshotProperty.Value) {
        throw "Join response did not advertise the published snapshot."
    }
    if (-not [string]::Equals([string]$snapshotProperty.Value.sha256, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Join snapshot metadata SHA256 mismatch."
    }

    Write-Host "Downloading snapshot with client token..."
    Invoke-WebRequest `
        -Method Get `
        -Uri "$baseUrl/api/session/$code/snapshot" `
        -Headers @{ Authorization = "Bearer $clientToken" } `
        -OutFile $tempDownload `
        -UseBasicParsing `
        -TimeoutSec 120

    $downloadHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $tempDownload).Hash
    if (-not [string]::Equals($downloadHash, $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Downloaded snapshot SHA256 mismatch. Source=$sourceHash Download=$downloadHash"
    }

    Write-Host ""
    Write-Host "PUBLIC RELAY SNAPSHOT PASS" -ForegroundColor Green
    Write-Host "SHA256: $sourceHash"
    Write-Host "Host upload -> Render -> client download is byte-identical."
}
catch {
    Write-Host ""
    Write-Host "PUBLIC RELAY SNAPSHOT FAIL" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    exit 1
}
finally {
    Remove-Item -LiteralPath $tempSource -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempDownload -Force -ErrorAction SilentlyContinue
}
