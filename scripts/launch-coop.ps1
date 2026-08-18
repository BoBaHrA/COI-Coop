param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Host", "Client")]
    [string]$Mode,

    [string]$CoiRoot = $env:COI_ROOT,

    [ValidateRange(1024, 65535)]
    [int]$Port = 27015,

    [switch]$Replay
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-CoiRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $mafi = Join-Path $Path "Captain of Industry_Data\Managed\Mafi.dll"
    $exe = Join-Path $Path "Captain of Industry.exe"
    return (Test-Path $mafi) -and (Test-Path $exe)
}

function Add-UniquePath($List, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
        if ((Test-Path -LiteralPath $Path) -and -not $List.Contains($Path)) {
            $List.Add($Path)
        }
    } catch { }
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    $registryCandidates = @(
        @{ Key = "HKEY_CURRENT_USER\Software\Valve\Steam"; Name = "SteamPath" },
        @{ Key = "HKEY_CURRENT_USER\Software\Valve\Steam"; Name = "SteamExe" },
        @{ Key = "HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"; Name = "InstallPath" },
        @{ Key = "HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"; Name = "InstallPath" }
    )

    foreach ($candidate in $registryCandidates) {
        try {
            $value = [Microsoft.Win32.Registry]::GetValue($candidate.Key, $candidate.Name, $null)
            if ($value) {
                $text = [string]$value
                if ($text.EndsWith("steam.exe", [StringComparison]::OrdinalIgnoreCase)) {
                    $text = Split-Path -Parent $text
                }
                Add-UniquePath $roots $text
            }
        } catch { }
    }

    if (${env:ProgramFiles(x86)}) { Add-UniquePath $roots (Join-Path ${env:ProgramFiles(x86)} "Steam") }
    if ($env:ProgramFiles) { Add-UniquePath $roots (Join-Path $env:ProgramFiles "Steam") }

    # Portable/custom Steam installs are common on gaming PCs. Probe only cheap,
    # conventional roots on each local filesystem drive; never recursively scan.
    foreach ($drive in Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue) {
        if (-not $drive.Root) { continue }
        Add-UniquePath $roots (Join-Path $drive.Root "Steam")
        Add-UniquePath $roots (Join-Path $drive.Root "SteamLibrary")
        Add-UniquePath $roots (Join-Path $drive.Root "Games\Steam")
        Add-UniquePath $roots (Join-Path $drive.Root "Games\SteamLibrary")
    }

    return $roots
}

function Find-CoiRoot {
    foreach ($steamRoot in Get-SteamRoots) {
        $libraries = New-Object System.Collections.Generic.List[string]
        $libraries.Add($steamRoot)

        $vdf = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $library = $Matches[1] -replace '\\\\', '\'
                    if ((Test-Path $library) -and -not $libraries.Contains($library)) {
                        $libraries.Add($library)
                    }
                }
            }
        }

        foreach ($library in $libraries) {
            $candidate = Join-Path $library "steamapps\common\Captain of Industry"
            if (Test-CoiRoot $candidate) {
                return (Resolve-Path $candidate).Path
            }
        }
    }

    return $null
}

if (-not (Test-CoiRoot $CoiRoot)) {
    Write-Host "COI_ROOT is not set to a valid game directory. Searching Steam libraries..."
    $CoiRoot = Find-CoiRoot
}

if (-not (Test-CoiRoot $CoiRoot)) {
    Write-Host ""
    Write-Host "Captain of Industry was not found automatically." -ForegroundColor Yellow
    Write-Host "In Steam: Captain of Industry -> Properties -> Installed Files -> Browse." -ForegroundColor Yellow
    Write-Host "Copy the folder path that contains 'Captain of Industry.exe'." -ForegroundColor Yellow
    Write-Host ""
    $manualRoot = Read-Host "Captain of Industry folder (leave empty to cancel)"
    if (-not [string]::IsNullOrWhiteSpace($manualRoot)) {
        $manualRoot = $manualRoot.Trim().Trim('"')
        if (Test-CoiRoot $manualRoot) {
            $CoiRoot = $manualRoot
        }
    }
}

if (-not (Test-CoiRoot $CoiRoot)) {
    throw "Captain of Industry was not found. The selected folder must contain 'Captain of Industry.exe' and 'Captain of Industry_Data\Managed\Mafi.dll'."
}

$resolvedRoot = (Resolve-Path $CoiRoot).Path
$exe = Join-Path $resolvedRoot "Captain of Industry.exe"
$networkMode = if ($Mode -eq "Host") { "1" } else { "2" }
$replayValue = if ($Replay) { "1" } else { "0" }

# Start-Process inherits this PowerShell process environment. These values are
# intentionally not persisted to the user's system environment.
$env:COI_COOP_MODE = $networkMode
$env:COI_COOP_PORT = [string]$Port
$env:COI_COOP_REPLAY = $replayValue

Write-Host "=== COI-Coop $Mode launcher ==="
Write-Host "Game:   $resolvedRoot"
Write-Host "Port:   $Port"
Write-Host "Replay: $replayValue"
if ($Replay) {
    Write-Host "WARNING: experimental authoritative replay is enabled for this game process only."
}
Write-Host ""

$process = Start-Process -FilePath $exe -WorkingDirectory $resolvedRoot -PassThru
Start-Sleep -Milliseconds 750
if ($process.HasExited) {
    throw "Captain of Industry process exited immediately with code $($process.ExitCode)."
}
Write-Host "Started Captain of Industry PID $($process.Id) as $Mode." -ForegroundColor Green
Write-Host "This launcher can now be closed; the game inherited the co-op environment."
