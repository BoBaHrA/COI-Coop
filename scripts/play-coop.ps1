param(
    [ValidateSet('Host', 'Join')][string]$Mode = 'Host',
    [string]$CoiRoot = $env:COI_ROOT,
    [string]$HostAddress,
    [string]$JoinKey,
    [ValidateRange(1024,65534)][int]$Port = 27015,
    [switch]$SelfTestHelpers
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'live-common.ps1')

function Get-BuildIdentity([string]$Root, [string]$ModDll) {
    $managed = Join-Path $Root 'Captain of Industry_Data\Managed'
    $text = "COI-LIVE-1`n"
    foreach ($name in @('Mafi.dll','Mafi.Core.dll','Mafi.Base.dll','Mafi.Unity.dll')) {
        $text += (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $managed $name)).Hash + "`n"
    }
    $text += (Get-FileHash -Algorithm SHA256 -LiteralPath $ModDll).Hash
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','') }
    finally { $sha.Dispose() }
}

function Read-WireString([IO.BinaryReader]$Reader) {
    # BinaryWriter's 7-bit UTF-8 length prefix, bounded before allocation.
    $length = 0
    $shift = 0
    do {
        $b = $Reader.ReadByte()
        if ($shift -ge 21) { throw 'Invalid snapshot header length.' }
        $length = $length -bor (($b -band 127) -shl $shift)
        $shift += 7
    } while (($b -band 128) -ne 0)
    if ($length -gt 1024) { throw 'Snapshot header is too long.' }
    $bytes = $Reader.ReadBytes($length)
    if ($bytes.Length -ne $length) { throw 'Snapshot header was truncated.' }
    return [Text.Encoding]::UTF8.GetString($bytes)
}

function Receive-World([string]$Address, [string]$Key, [string]$BuildId, [string]$Destination) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect($Address, $Port + 1, $null, $null)
        try {
            if (-not $connect.AsyncWaitHandle.WaitOne(10000)) { throw 'Host did not answer. Check address and TCP ports.' }
            $client.EndConnect($connect)
        } finally { $connect.AsyncWaitHandle.Close() }
        $client.ReceiveTimeout = 60000
        $client.SendTimeout = 10000
        $stream = $client.GetStream()
        $header = [Text.Encoding]::ASCII.GetBytes("COI_LIVE|1|$Key|$BuildId`n")
        $stream.Write($header,0,$header.Length)
        $reader = New-Object IO.BinaryReader($stream)
        $response = Read-WireString $reader
        if ($response -ne 'COI_LIVE_SNAPSHOT_1') { throw $response }
        $remoteBuild = Read-WireString $reader
        $ticket = Read-WireString $reader
        $hash = Read-WireString $reader
        $step = $reader.ReadInt64()
        $paused = $reader.ReadBoolean()
        $length = $reader.ReadInt32()
        if ($remoteBuild -cne $BuildId -or $ticket -cnotmatch '^[A-F0-9]{64}$' -or $hash -cnotmatch '^[A-F0-9]{64}$' -or $step -lt 0) {
            throw 'Invalid world snapshot identity.'
        }
        if ($length -le 0 -or $length -gt 268435456) { throw 'Invalid world snapshot size.' }
        $file = [IO.File]::Create($Destination)
        try {
            $remaining = $length
            while ($remaining -gt 0) {
                $bytes = $reader.ReadBytes([Math]::Min(65536,$remaining))
                if ($bytes.Length -eq 0) { throw 'Host disconnected during world transfer.' }
                $file.Write($bytes,0,$bytes.Length)
                $remaining -= $bytes.Length
            }
        } finally { $file.Dispose() }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash -cne $hash) { throw 'World snapshot checksum mismatch.' }
        return [PSCustomObject]@{ Ticket=$ticket; Hash=$hash; Step=$step; Paused=$paused }
    } finally { $client.Close() }
}

if ($SelfTestHelpers) {
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write('COI_LIVE_SNAPSHOT_1')
    $writer.Write(('A' * 128))
    $writer.Flush()
    $stream.Position = 0
    $reader = New-Object IO.BinaryReader($stream)
    if ((Read-WireString $reader) -cne 'COI_LIVE_SNAPSHOT_1') { throw 'Header decode failed' }
    if ((Read-WireString $reader) -cne ('A' * 128)) { throw 'Multi-byte length decode failed' }
    $reader.Dispose()
    $bad = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($bad)
    $writer.Write(('B' * 1025))
    $writer.Flush()
    $bad.Position = 0
    $reader = New-Object IO.BinaryReader($bad)
    $rejected = $false
    try { Read-WireString $reader | Out-Null } catch { $rejected = $true }
    $reader.Dispose()
    if (-not $rejected) { throw 'Oversized header accepted' }
    Write-Host 'PASS live launcher wire decoding and length limit'
    return
}

$cacheDir = $null
$statusPath = Join-Path $env:TEMP ('coi-live-' + [Guid]::NewGuid().ToString('N') + '.status')
$process = $null
try {
    if (Get-Process -Name 'Captain of Industry' -ErrorAction SilentlyContinue) { throw 'Close Captain of Industry before starting Host or Join.' }
    if (-not (Test-CoiRoot $CoiRoot)) { $CoiRoot = Find-CoiRoot }
    if (-not (Test-CoiRoot $CoiRoot)) { $CoiRoot = (Read-Host 'Captain of Industry installation folder').Trim('"') }
    if (-not (Test-CoiRoot $CoiRoot)) { throw 'Game installation not found.' }
    $CoiRoot = (Resolve-Path -LiteralPath $CoiRoot).Path
    $modDir = Join-Path $env:APPDATA 'Captain of Industry\Mods\CoiCoop'
    $packaged = Join-Path (Split-Path -Parent $PSScriptRoot) 'CoiCoop'
    if (Test-Path -LiteralPath (Join-Path $packaged 'CoiCoop.dll')) {
        if (Test-Path -LiteralPath (Join-Path $modDir 'CoiCoop.dll')) {
            $backup = Join-Path $env:APPDATA ('COI-Coop-Backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Copy-Item -LiteralPath $modDir -Destination $backup -Recurse
        }
        New-Item -ItemType Directory -Path $modDir -Force | Out-Null
        foreach ($name in @('CoiCoop.dll','manifest.json','config.json')) {
            Copy-Item -LiteralPath (Join-Path $packaged $name) -Destination (Join-Path $modDir $name) -Force
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $modDir 'CoiCoop.dll'))) { throw 'Install the supplied CoiCoop mod or run build-mod.ps1 first.' }
    $buildId = Get-BuildIdentity $CoiRoot (Join-Path $modDir 'CoiCoop.dll')
    # Process-local settings. Legacy relay/sidecar transports do not participate.
    foreach ($entry in @(Get-ChildItem Env: | Where-Object { $_.Name.StartsWith('COI_COOP_') })) { Remove-Item ('Env:' + $entry.Name) }
    $env:COI_COOP_LIVE = '1'
    $env:COI_COOP_PORT = [string]$Port
    $env:COI_COOP_GAME_ROOT = $CoiRoot
    $env:COI_COOP_STATUS_PATH = $statusPath
    $argsForGame = @('--skip-splash-screen')

    if ($Mode -eq 'Host') {
        $env:COI_COOP_MODE = '1'
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        $bytes = New-Object byte[] 32
        try { $random.GetBytes($bytes) } finally { $random.Dispose() }
        $env:COI_COOP_JOIN_KEY = [BitConverter]::ToString($bytes).Replace('-','')
        Write-Host 'COI CO-OP — HOST' -ForegroundColor Cyan
        Write-Host 'Enable COI Co-op, then load your normal save. You can play alone.'
        Write-Host 'Give your friend your LAN/VPN address (or forwarded public address) and this key:'
        Write-Host $env:COI_COOP_JOIN_KEY -ForegroundColor Green
        Write-Host "TCP ports: $Port and $($Port + 1). Allow the game on your private network."
        Write-Host 'The world pauses briefly while a friend downloads and loads its current state.'
    }
    else {
        $env:COI_COOP_MODE = '2'
        if ([string]::IsNullOrWhiteSpace($HostAddress)) { $HostAddress = Read-Host 'Host address' }
        if ([string]::IsNullOrWhiteSpace($JoinKey)) { $JoinKey = Read-Host 'Connection key from host' }
        $JoinKey = $JoinKey.Trim().ToUpperInvariant()
        if ($JoinKey -cnotmatch '^[A-F0-9]{64}$') { throw 'Connection key must contain 64 hexadecimal characters.' }
        $saveRoot = Join-Path $env:APPDATA 'Captain of Industry\Saves'
        New-Item -ItemType Directory -Path $saveRoot -Force | Out-Null
        # Only our own marked caches are removed, and only while the game is closed.
        foreach ($dir in @(Get-ChildItem -LiteralPath $saveRoot -Directory -Filter '__COI_COOP_LIVE_*')) {
            if (Test-Path -LiteralPath (Join-Path $dir.FullName '.coi-coop-live-cache')) { Remove-Item -LiteralPath $dir.FullName -Recurse -Force }
        }
        $cacheName = '__COI_COOP_LIVE_' + [Guid]::NewGuid().ToString('N')
        $cacheDir = Join-Path $saveRoot $cacheName
        New-Item -ItemType Directory -Path $cacheDir | Out-Null
        Set-Content -LiteralPath (Join-Path $cacheDir '.coi-coop-live-cache') -Value 'Disposable host-world cache. Not a separate campaign.'
        $cachePath = Join-Path $cacheDir ($cacheName + '.save')
        Write-Host 'Requesting the current host world...' -ForegroundColor Cyan
        $snapshot = Receive-World $HostAddress $JoinKey $buildId $cachePath
        $env:COI_COOP_HOST = $HostAddress.Trim()
        $env:COI_COOP_BUILD_ID = $buildId
        $env:COI_COOP_TICKET = $snapshot.Ticket
        $env:COI_COOP_BASELINE_SHA256 = $snapshot.Hash
        $env:COI_COOP_SNAPSHOT_STEP = [string]$snapshot.Step
        $env:COI_COOP_SNAPSHOT_PAUSED = if ($snapshot.Paused) { '1' } else { '0' }
        $env:COI_COOP_SESSION_CACHE_PATH = $cachePath
        $env:COI_COOP_SESSION_CACHE_NAME = $cacheName
        # Supported by the supplied GameMainMb. Ensure this is the latest save
        # without changing any existing save's content or timestamp.
        $latest = Get-ChildItem -LiteralPath $saveRoot -Filter '*.save' -File -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $stamp = [DateTime]::UtcNow
        if ($latest -and $latest.LastWriteTimeUtc -ge $stamp) { $stamp = $latest.LastWriteTimeUtc.AddSeconds(1) }
        [IO.File]::SetLastWriteTimeUtc($cachePath,$stamp)
        $argsForGame += '--launch-last-game'
        Write-Host 'World verified. Starting CoI and loading the host snapshot automatically.' -ForegroundColor Green
    }
    $process = Start-Process -FilePath (Join-Path $CoiRoot 'Captain of Industry.exe') -WorkingDirectory $CoiRoot -ArgumentList $argsForGame -PassThru
    $lastStatus = ''
    Write-Host 'Keep this window open for connection status and automatic guest-cache cleanup.'
    while (-not $process.HasExited) {
        if (Test-Path -LiteralPath $statusPath) {
            $status = Get-Content -LiteralPath $statusPath -Raw -ErrorAction SilentlyContinue
            if ($status -and $status -ne $lastStatus) { Write-Host $status -ForegroundColor Cyan; $lastStatus = $status }
        }
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if ([string]::IsNullOrWhiteSpace($lastStatus)) { throw 'CoI exited before initializing live co-op. Check that the mod is enabled and inspect the game log.' }
}
finally {
    if ($cacheDir -and ($null -eq $process -or $process.HasExited)) {
        if (Test-Path -LiteralPath (Join-Path $cacheDir '.coi-coop-live-cache')) { Remove-Item -LiteralPath $cacheDir -Recurse -Force }
    }
    Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue
}
