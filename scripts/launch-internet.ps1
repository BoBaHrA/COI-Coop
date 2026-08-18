param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Host", "Client")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$RelayUrl,

    [string]$SessionCode,

    [string]$SourceSaveName = "COOP_LAN_BASE",

    [string]$CoiRoot = $env:COI_ROOT,

    [ValidateRange(1024, 65535)]
    [int]$Port = 27015
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$SessionCachePrefix = "__COI_COOP_SESSION_"
$SessionCacheMarker = ".coi-coop-session-cache"

function Normalize-RelayBase([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Relay URL is required."
    }

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

function To-WebSocketUrl([string]$BaseUrl) {
    $uri = [Uri]$BaseUrl
    $scheme = if ($uri.Scheme -eq "https") { "wss" } else { "ws" }
    return "${scheme}://$($uri.Authority)/relay"
}

function Get-HttpStatusCodeFromError($ErrorRecord) {
    try {
        if ($ErrorRecord -and $ErrorRecord.Exception -and $ErrorRecord.Exception.Response) {
            return [int]$ErrorRecord.Exception.Response.StatusCode
        }
    }
    catch { }
    return 0
}

function Resolve-SourceSave([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Source save name/path is required for Host mode."
    }

    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Value).Path
    }

    $saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
    if (-not (Test-Path -LiteralPath $saveRoot -PathType Container)) {
        throw "Captain of Industry save directory was not found: $saveRoot"
    }

    $leaf = [IO.Path]::GetFileName($Value)
    if (-not $leaf.EndsWith(".save", [StringComparison]::OrdinalIgnoreCase)) {
        $leaf += ".save"
    }

    $matches = @(Get-ChildItem -LiteralPath $saveRoot -File -Recurse -Filter $leaf -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        throw "Source save '$Value' was not found under: $saveRoot"
    }
    if ($matches.Count -gt 1) {
        Write-Host "More than one save named '$leaf' was found:" -ForegroundColor Yellow
        foreach ($match in $matches) { Write-Host ("  " + $match.FullName) }
        throw "Pass an exact .save path to -SourceSaveName."
    }

    return $matches[0].FullName
}

function Publish-SessionSnapshot(
    [string]$BaseUrl,
    [string]$Code,
    [string]$HostToken,
    [string]$SavePath) {

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $SavePath).Hash
    $headers = @{
        Authorization = "Bearer $HostToken"
        "X-COI-Save-Name" = [IO.Path]::GetFileName($SavePath)
    }

    Write-Host "Publishing canonical host-world snapshot to relay..."
    $web = Invoke-WebRequest `
        -Method Put `
        -Uri "$BaseUrl/api/session/$Code/snapshot" `
        -Headers $headers `
        -ContentType "application/octet-stream" `
        -InFile $SavePath `
        -UseBasicParsing `
        -TimeoutSec 120

    $meta = $web.Content | ConvertFrom-Json
    $metaHash = ""
    if ($null -ne $meta) {
        $metaHash = [string]($meta.sha256)
    }
    if ($null -eq $meta -or [string]::IsNullOrWhiteSpace($metaHash) -or -not [string]::Equals($metaHash, $hash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Relay snapshot hash did not match the canonical host save."
    }

    Write-Host ("Snapshot: {0} bytes" -f [string]($meta.size))
    Write-Host "SHA256:   $hash" -ForegroundColor Green
    return $meta
}

function Get-SnapshotMetaFromJoin($JoinResponse) {
    if ($null -eq $JoinResponse) { return $null }
    $property = $JoinResponse.PSObject.Properties["snapshot"]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-SessionCacheName([string]$Code) {
    $raw = ($Code.ToUpperInvariant() -replace '[^A-Z0-9]', '')
    if ($raw.Length -ne 8) {
        throw "Cannot create session cache name from invalid session code '$Code'."
    }
    return $SessionCachePrefix + $raw
}

function Remove-StaleSessionCaches([string]$SaveRoot, [string]$KeepCacheName) {
    if (-not (Test-Path -LiteralPath $SaveRoot -PathType Container)) { return }

    $directories = @(Get-ChildItem -LiteralPath $SaveRoot -Directory -Filter ($SessionCachePrefix + "*") -ErrorAction SilentlyContinue)
    foreach ($directory in $directories) {
        if (-not [string]::IsNullOrWhiteSpace($KeepCacheName)
            -and [string]::Equals($directory.Name, $KeepCacheName, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $marker = Join-Path $directory.FullName $SessionCacheMarker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            continue
        }

        try {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "Removed stale COI-Coop session cache: $($directory.Name)"
        }
        catch {
            Write-Host "Could not remove stale session cache '$($directory.Name)': $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Download-SessionSnapshot(
    [string]$BaseUrl,
    [string]$Code,
    [string]$ClientToken,
    $SnapshotMeta) {

    if ($null -eq $SnapshotMeta) {
        throw "This session has no canonical host snapshot. Ask the host to use the current Internet launcher."
    }

    $metaHash = [string]($SnapshotMeta.sha256)
    $metaName = [string]($SnapshotMeta.name)
    if ([string]::IsNullOrWhiteSpace($metaHash) -or [string]::IsNullOrWhiteSpace($metaName)) {
        throw "This session has invalid host snapshot metadata. Ask the host to create a new session."
    }

    $expectedHash = $metaHash.ToUpperInvariant()
    $cacheName = Get-SessionCacheName $Code
    $saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
    $cacheDir = Join-Path $saveRoot $cacheName
    $destination = Join-Path $cacheDir ($cacheName + ".save")
    $marker = Join-Path $cacheDir $SessionCacheMarker
    $temp = Join-Path $env:TEMP ("coi-coop-snapshot-" + [Guid]::NewGuid().ToString("N") + ".save")

    New-Item -ItemType Directory -Path $saveRoot -Force | Out-Null
    Remove-StaleSessionCaches -SaveRoot $saveRoot -KeepCacheName $cacheName
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

    try {
        Write-Host "Downloading canonical host-world snapshot..."
        Invoke-WebRequest `
            -Method Get `
            -Uri "$BaseUrl/api/session/$Code/snapshot" `
            -Headers @{ Authorization = "Bearer $ClientToken" } `
            -OutFile $temp `
            -UseBasicParsing `
            -TimeoutSec 120

        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temp).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Downloaded host snapshot SHA256 mismatch. Expected $expectedHash but got $actualHash."
        }

        Move-Item -LiteralPath $temp -Destination $destination -Force
        $markerText = @(
            "COI-Coop disposable session cache",
            "session=$Code",
            "hostSnapshotName=$metaName",
            "sha256=$actualHash",
            "created=" + (Get-Date -Format o),
            "This directory is not an independent campaign save and may be deleted by COI-Coop."
        )
        Set-Content -LiteralPath $marker -Value $markerText -Encoding UTF8

        Write-Host "Host snapshot SHA256 verified." -ForegroundColor Green
        Write-Host "Prepared temporary session cache: $destination"
        return [PSCustomObject]@{
            Path = $destination
            LoadName = $cacheName
            GameName = $cacheName
            HostSnapshotName = $metaName
            SHA256 = $actualHash
        }
    }
    finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

try {
    $baseUrl = Normalize-RelayBase $RelayUrl
    $wsUrl = To-WebSocketUrl $baseUrl
    $token = $null
    $code = $null
    $expiresAt = $null
    $loadSaveName = $null
    $sessionCachePath = $null
    $sessionCacheName = $null
    $sessionCacheGameName = $null
    $hostSnapshotName = $null
    $baselineSha256 = $null

    Write-Host "=== COI-Coop INTERNET $Mode ==="
    Write-Host "Relay: $baseUrl"
    Write-Host ""

    if ($Mode -eq "Host") {
        $sourceSavePath = Resolve-SourceSave $SourceSaveName
        $loadSaveName = [IO.Path]::GetFileNameWithoutExtension($sourceSavePath)
        $hostSnapshotName = [IO.Path]::GetFileName($sourceSavePath)
        Write-Host "Canonical host world: $sourceSavePath"
        Write-Host "Creating co-op session..."
        $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60

        $code = Normalize-SessionCode ([string]($response.code))
        $token = [string]($response.hostToken)
        $expiresAt = [string]($response.expiresAt)

        if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($token)) {
            throw "Relay returned an invalid host session response."
        }

        $publishedSnapshot = Publish-SessionSnapshot -BaseUrl $baseUrl -Code $code -HostToken $token -SavePath $sourceSavePath
        $baselineSha256 = [string]($publishedSnapshot.sha256)

        Write-Host ""
        Write-Host "=========================================" -ForegroundColor Cyan
        Write-Host "  SESSION CODE:  $code" -ForegroundColor Green
        Write-Host "=========================================" -ForegroundColor Cyan
        Write-Host "Send ONLY this code to your friend." -ForegroundColor Yellow
        if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
            Write-Host "Expires: $expiresAt"
        }
        Write-Host ""
        Write-Host "This HOST save is the only canonical campaign world." -ForegroundColor Green
        Write-Host "The client receives a disposable verified mirror; it does not own a second campaign save."
        Write-Host "1. Your friend starts Join with this code."
        Write-Host "2. Their launcher downloads and verifies the host-world snapshot."
        Write-Host "3. Current conservative prototype waits until the client mirror is loaded."
        Write-Host "4. Only then press ENTER here to start the HOST game." -ForegroundColor Green
        Write-Host ""
        Write-Host "If gameplay disconnects, this code is intentionally invalidated." -ForegroundColor Yellow
        Write-Host "For recovery: save the current HOST world and create a NEW session from that host save."
        Write-Host ""
        [void](Read-Host "Press ENTER after the client reports the host world is loaded")
        Write-Host "Starting host Captain of Industry..."
        Write-Host "CANONICAL HOST WORLD: $loadSaveName" -ForegroundColor Yellow
        Write-Host ""
    }
    else {
        if ([string]::IsNullOrWhiteSpace($SessionCode)) {
            $SessionCode = Read-Host "Session code"
        }
        $code = Normalize-SessionCode $SessionCode
        if ([string]::IsNullOrWhiteSpace($code)) {
            throw "Session code must contain exactly 8 letters/digits (for example ABCD-2345)."
        }

        Write-Host "Joining host world for session $code..."
        try {
            $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60
        }
        catch {
            $status = Get-HttpStatusCodeFromError $_
            if ($status -eq 409) {
                throw "RESYNC REQUIRED: this host session disconnected. Ask the host to save the current canonical world and create a NEW session; Join will fetch that fresh host snapshot automatically."
            }
            throw
        }

        $token = [string]($response.clientToken)
        $expiresAt = [string]($response.expiresAt)
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "Relay returned an invalid client session response."
        }

        $snapshotMeta = Get-SnapshotMetaFromJoin $response
        $installedSnapshot = Download-SessionSnapshot -BaseUrl $baseUrl -Code $code -ClientToken $token -SnapshotMeta $snapshotMeta
        $loadSaveName = [string]($installedSnapshot.LoadName)
        $sessionCacheName = [string]($installedSnapshot.LoadName)
        $sessionCacheGameName = [string]($installedSnapshot.GameName)
        $sessionCachePath = [string]($installedSnapshot.Path)
        $hostSnapshotName = [string]($installedSnapshot.HostSnapshotName)
        $baselineSha256 = [string]($installedSnapshot.SHA256)

        Write-Host "Session accepted." -ForegroundColor Green
        if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
            Write-Host "Expires: $expiresAt"
        }
        Write-Host "Temporary host-world mirror prepared." -ForegroundColor Green
        Write-Host "SESSION CACHE: $loadSaveName" -ForegroundColor Yellow
        Write-Host ("SHA256: " + $baselineSha256) -ForegroundColor Green
        Write-Host "This cache is disposable and is NOT a client-owned campaign save." -ForegroundColor Cyan
        Write-Host ""
    }

    if ([string]::IsNullOrWhiteSpace($baselineSha256)) {
        throw "Verified host snapshot SHA256 is missing; refusing to launch an unbound gameplay session."
    }

    # These values exist only in this launcher process and the Captain of Industry
    # child process. Session tokens are never persisted to disk.
    $env:COI_COOP_LAN = "0"
    $env:COI_COOP_RELAY = "1"
    $env:COI_COOP_RELAY_WS = $wsUrl
    $env:COI_COOP_SESSION_CODE = $code
    $env:COI_COOP_SESSION_TOKEN = $token
    $env:COI_COOP_HOST_SNAPSHOT_NAME = $hostSnapshotName
    $env:COI_COOP_BASELINE_SHA256 = $baselineSha256.ToUpperInvariant()

    if ($Mode -eq "Client") {
        $env:COI_COOP_SESSION_CACHE_PATH = $sessionCachePath
        $env:COI_COOP_SESSION_CACHE_NAME = $sessionCacheName
        $env:COI_COOP_SESSION_CACHE_GAME = $sessionCacheGameName
        $env:COI_COOP_AUTOJOIN = "1"
    }
    else {
        Remove-Item Env:COI_COOP_SESSION_CACHE_PATH -ErrorAction SilentlyContinue
        Remove-Item Env:COI_COOP_SESSION_CACHE_NAME -ErrorAction SilentlyContinue
        Remove-Item Env:COI_COOP_SESSION_CACHE_GAME -ErrorAction SilentlyContinue
        Remove-Item Env:COI_COOP_AUTOJOIN -ErrorAction SilentlyContinue
    }

    $launcher = Join-Path $PSScriptRoot "launch-coop.ps1"
    & $launcher -Mode $Mode -CoiRoot $CoiRoot -Port $Port -Replay
    exit 0
}
catch {
    Write-Host ""
    Write-Host "COI-Coop internet launcher failed:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    exit 1
}
