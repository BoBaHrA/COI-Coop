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

