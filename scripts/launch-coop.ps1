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

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    $registryCandidates = @(
        @{ Key = "HKEY_CURRENT_USER\Software\Valve\Steam"; Name = "SteamPath" },
        @{ Key = "HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"; Name = "InstallPath" },
        @{ Key = "HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"; Name = "InstallPath" }
    )

    foreach ($candidate in $registryCandidates) {
        try {
            $value = [Microsoft.Win32.Registry]::GetValue($candidate.Key, $candidate.Name, $null)
            if ($value -and (Test-Path $value) -and -not $roots.Contains($value)) {
                $roots.Add($value)
            }
        } catch { }
    }

    $commonCandidates = @()
    if (${env:ProgramFiles(x86)}) {
        $commonCandidates += (Join-Path ${env:ProgramFiles(x86)} "Steam")
    }
    if ($env:ProgramFiles) {
        $commonCandidates += (Join-Path $env:ProgramFiles "Steam")
    }

    foreach ($path in $commonCandidates) {
        if ((Test-Path $path) -and -not $roots.Contains($path)) {
            $roots.Add($path)
        }
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
    throw "Captain of Industry was not found automatically. Re-run with -CoiRoot 'C:\path\to\Captain of Industry'."
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
Write-Host "Started Captain of Industry PID $($process.Id) as $Mode."
Write-Host "This launcher can now be closed; the game inherited the co-op environment."
