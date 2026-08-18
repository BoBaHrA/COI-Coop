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

    Write-Host "Publishing synchronized starting save to relay..."
    $web = Invoke-WebRequest `
        -Method Put `
        -Uri "$BaseUrl/api/session/$Code/snapshot" `
        -Headers $headers `
        -ContentType "application/octet-stream" `
        -InFile $SavePath `
        -UseBasicParsing `
        -TimeoutSec 120

    $meta = $web.Content | ConvertFrom-Json
    if ($null -eq $meta
        -or [string]::IsNullOrWhiteSpace([string]$meta.sha256)
        -or -not [string]::Equals([string]$meta.sha256, $hash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Relay snapshot hash did not match the local host save."
    }

    Write-Host ("Snapshot: {0} bytes" -f [string]$meta.size)
    Write-Host "SHA256:   $hash" -ForegroundColor Green
    return $meta
}

function Get-SnapshotMetaFromJoin($JoinResponse) {
    if ($null -eq $JoinResponse) { return $null }
    $property = $JoinResponse.PSObject.Properties["snapshot"]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ClientSaveBaseName([string]$SnapshotName) {
    $baseName = [IO.Path]::GetFileNameWithoutExtension($SnapshotName)
    if ($baseName.EndsWith("_HOST", [StringComparison]::OrdinalIgnoreCase)) {
        $baseName = $baseName.Substring(0, $baseName.Length - 5)
    }
    if ($baseName.EndsWith("_CLIENT", [StringComparison]::OrdinalIgnoreCase)) {
        $baseName = $baseName.Substring(0, $baseName.Length - 7)
    }
    if ([string]::IsNullOrWhiteSpace($baseName)) { $baseName = "COI_COOP_SYNC" }
    return $baseName
}

function Download-SessionSnapshot(
    [string]$BaseUrl,
    [string]$Code,
    [string]$ClientToken,
    $SnapshotMeta) {

    if ($null -eq $SnapshotMeta
        -or [string]::IsNullOrWhiteSpace([string]$SnapshotMeta.sha256)
        -or [string]::IsNullOrWhiteSpace([string]$SnapshotMeta.name)) {
        throw "This session has no synchronized host snapshot. Ask the host to use the current Internet launcher."
    }

    $expectedHash = ([string]$SnapshotMeta.sha256).ToUpperInvariant()
    $baseName = Get-ClientSaveBaseName ([string]$SnapshotMeta.name)
    $clientLeaf = $baseName + "_CLIENT.save"
    $saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
    $saveDir = Join-Path $saveRoot $baseName
    $destination = Join-Path $saveDir $clientLeaf
    $temp = Join-Path $env:TEMP ("coi-coop-snapshot-" + [Guid]::NewGuid().ToString("N") + ".save")

    New-Item -ItemType Directory -Path $saveDir -Force | Out-Null
    try {
        Write-Host "Downloading synchronized host snapshot..."
        Invoke-WebRequest `
            -Method Get `
            -Uri "$BaseUrl/api/session/$Code/snapshot" `
            -Headers @{ Authorization = "Bearer $ClientToken" } `
            -OutFile $temp `
            -UseBasicParsing `
            -TimeoutSec 120

        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temp).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Downloaded snapshot SHA256 mismatch. Expected $expectedHash but got $actualHash."
        }

        Move-Item -LiteralPath $temp -Destination $destination -Force
        Write-Host "Snapshot SHA256 verified." -ForegroundColor Green
        Write-Host "Installed client save: $destination"
        return [PSCustomObject]@{
            Path = $destination
            LoadName = [IO.Path]::GetFileNameWithoutExtension($destination)
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

    Write-Host "=== COI-Coop INTERNET $Mode ==="
    Write-Host "Relay: $baseUrl"
    Write-Host ""

    if ($Mode -eq "Host") {
        $sourceSavePath = Resolve-SourceSave $SourceSaveName
        $loadSaveName = [IO.Path]::GetFileNameWithoutExtension($sourceSavePath)
        Write-Host "Host save: $sourceSavePath"
        Write-Host "Creating co-op session..."
        $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session" -ContentType "application/json" -Body "{}" -TimeoutSec 60

        $code = Normalize-SessionCode ([string]$response.code)
        $token = [string]$response.hostToken
        $expiresAt = [string]$response.expiresAt

        if ([string]::IsNullOrWhiteSpace($code) -or [string]::IsNullOrWhiteSpace($token)) {
            throw "Relay returned an invalid host session response."
        }

        [void](Publish-SessionSnapshot -BaseUrl $baseUrl -Code $code -HostToken $token -SavePath $sourceSavePath)

        Write-Host ""
        Write-Host "=========================================" -ForegroundColor Cyan
        Write-Host "  SESSION CODE:  $code" -ForegroundColor Green
        Write-Host "=========================================" -ForegroundColor Cyan
        Write-Host "Send ONLY this code to your friend." -ForegroundColor Yellow
        if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
            Write-Host "Expires: $expiresAt"
        }
        Write-Host ""
        Write-Host "The client launcher will download and SHA256-verify this exact host save automatically." -ForegroundColor Green
        Write-Host "1. Your friend starts the Internet client with this code."
        Write-Host "2. Their launcher prints the synchronized *_CLIENT save name."
        Write-Host "3. Your friend loads that save and waits in the world."
        Write-Host "4. Only then press ENTER here to start the HOST game." -ForegroundColor Green
        Write-Host ""
        Write-Host "If gameplay disconnects, this code is intentionally invalidated." -ForegroundColor Yellow
        Write-Host "For conservative recovery: save the current HOST world, then create a NEW session from that save."
        Write-Host ""
        [void](Read-Host "Press ENTER after the client synchronized save is loaded")
        Write-Host "Starting host Captain of Industry..."
        Write-Host "LOAD HOST SAVE: $loadSaveName" -ForegroundColor Yellow
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

        Write-Host "Joining session $code..."
        try {
            $response = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/session/$code/join" -ContentType "application/json" -Body "{}" -TimeoutSec 60
        }
        catch {
            $status = Get-HttpStatusCodeFromError $_
            if ($status -eq 409) {
                throw "RESYNC REQUIRED: gameplay in session $code was already connected and then disconnected. Reusing this code with an old save is unsafe. Ask the host to save the current world and create a NEW session; the new launcher will transfer that fresh snapshot automatically."
            }
            throw
        }

        $token = [string]$response.clientToken
        $expiresAt = [string]$response.expiresAt
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "Relay returned an invalid client session response."
        }

        $snapshotMeta = Get-SnapshotMetaFromJoin $response
        $installedSnapshot = Download-SessionSnapshot -BaseUrl $baseUrl -Code $code -ClientToken $token -SnapshotMeta $snapshotMeta
        $loadSaveName = [string]$installedSnapshot.LoadName

        Write-Host "Session accepted." -ForegroundColor Green
        if (-not [string]::IsNullOrWhiteSpace($expiresAt)) {
            Write-Host "Expires: $expiresAt"
        }
        Write-Host "LOAD THIS SAVE: $loadSaveName" -ForegroundColor Yellow
        Write-Host "SHA256: $($installedSnapshot.SHA256)" -ForegroundColor Green
        Write-Host ""
    }

    # These values exist only in this launcher process and the Captain of Industry
    # child process. Session tokens are never persisted to disk.
    $env:COI_COOP_LAN = "0"
    $env:COI_COOP_RELAY = "1"
    $env:COI_COOP_RELAY_WS = $wsUrl
    $env:COI_COOP_SESSION_CODE = $code
    $env:COI_COOP_SESSION_TOKEN = $token

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
