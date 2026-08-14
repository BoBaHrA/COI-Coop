param(
    [string]$CoiRoot = $env:COI_ROOT,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
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
    throw "Captain of Industry was not found automatically. Re-run with: .\scripts\build-mod.ps1 -CoiRoot 'C:\path\to\Captain of Industry'"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw ".NET SDK was not found. Install .NET 8 SDK (or newer) and reopen PowerShell."
}

$env:COI_ROOT = (Resolve-Path $CoiRoot).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "src\CoiCoop\CoiCoop.csproj"

Write-Host "COI_ROOT: $env:COI_ROOT"
Write-Host "dotnet: $(& dotnet --version)"
Write-Host "Building COI-Coop ($Configuration)..."

& dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "COI-Coop build failed with exit code $LASTEXITCODE."
}

$modDir = Join-Path $env:APPDATA "Captain of Industry\Mods\CoiCoop"
Write-Host ""
Write-Host "BUILD SUCCEEDED"
Write-Host "Mod deployed to: $modDir"
Write-Host "Next: launch Captain of Industry, enable 'COI Co-op Prototype', load a save, and inspect the latest log in %APPDATA%\Captain of Industry\Logs."
