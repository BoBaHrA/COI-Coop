param(
    [Parameter(Mandatory = $true)]
    [string]$RelayUrl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$modRoot = Join-Path $env:APPDATA "Captain of Industry\Mods\CoiCoop"
$distRoot = Join-Path $repoRoot "dist"
$stageRoot = Join-Path $distRoot "COI-Coop-Internet-Client"
$zipPath = Join-Path $distRoot "COI-Coop-Internet-Client.zip"

$relayBase = $RelayUrl.Trim().TrimEnd('/')
$relayUri = $null
$relayUriIsValid = [Uri]::TryCreate($relayBase, [UriKind]::Absolute, [ref]$relayUri)
if (-not $relayUriIsValid -or ($relayUri.Scheme -ne "https" -and $relayUri.Scheme -ne "http")) {
    throw "RelayUrl must be a valid https:// URL (or http:// for local development)."
}

if (-not (Test-Path $modRoot)) {
    throw "Deployed COI-Coop mod was not found: $modRoot. Run scripts\build-mod.bat first."
}

$requiredModFiles = @("CoiCoop.dll", "manifest.json", "config.json")
foreach ($name in $requiredModFiles) {
    $path = Join-Path $modRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required deployed mod file is missing: $path"
    }
}

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$stageScripts = Join-Path $stageRoot "scripts"
$stageMod = Join-Path $stageRoot "payload\CoiCoop"
New-Item -ItemType Directory -Path $stageScripts -Force | Out-Null
New-Item -ItemType Directory -Path $stageMod -Force | Out-Null

Copy-Item (Join-Path $PSScriptRoot "launch-coop.ps1") $stageScripts -Force
Copy-Item (Join-Path $PSScriptRoot "launch-internet.ps1") $stageScripts -Force
foreach ($name in $requiredModFiles) {
    Copy-Item (Join-Path $modRoot $name) (Join-Path $stageMod $name) -Force
}

$dllHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stageMod "CoiCoop.dll")).Hash

$installer = @"
@echo off
setlocal
cd /d "%~dp0"

echo === COI-Coop INTERNET CLIENT installer ===
echo.

set "MODDST=%APPDATA%\Captain of Industry\Mods\CoiCoop"

if not exist "%MODDST%" mkdir "%MODDST%"

copy /Y "payload\CoiCoop\CoiCoop.dll" "%MODDST%\CoiCoop.dll" >nul || goto :installfail
copy /Y "payload\CoiCoop\manifest.json" "%MODDST%\manifest.json" >nul || goto :installfail
copy /Y "payload\CoiCoop\config.json" "%MODDST%\config.json" >nul || goto :installfail

echo Mod installed to: %MODDST%
echo Relay: $relayBase
echo.
echo Enter the SESSION CODE received from the host.
echo The launcher will download and SHA256-verify the current host snapshot automatically.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\launch-internet.ps1" -Mode Client -RelayUrl "$relayBase"
set "LAUNCHCODE=%ERRORLEVEL%"
if not "%LAUNCHCODE%"=="0" goto :launchfail

echo.
echo Captain of Industry launch command completed successfully.
timeout /t 2 /nobreak >nul
exit /b 0

:launchfail
echo.
echo ============================================================
echo COI-COOP CLIENT LAUNCH FAILED with exit code %LAUNCHCODE%.
echo Keep this window open and send a screenshot of everything above.
echo ============================================================
echo.
pause
exit /b %LAUNCHCODE%

:installfail
echo.
echo INSTALL FAILED while copying mod files.
echo Keep this window open and send a screenshot of everything above.
echo.
pause
exit /b 1
"@
Set-Content -LiteralPath (Join-Path $stageRoot "INSTALL_AND_LAUNCH.bat") -Value $installer -Encoding ASCII

$readme = @"
COI-Coop internet client test kit

1. Extract this ZIP to a normal folder.
2. Make sure Captain of Industry is closed.
3. Run INSTALL_AND_LAUNCH.bat.
4. Enter the session code sent by the host.
5. The launcher downloads the host's current synchronized save and verifies its SHA256 automatically.
6. If the launcher cannot find Captain of Industry automatically, use Steam -> Captain of Industry -> Properties -> Installed Files -> Browse and paste that folder path.
7. In Captain of Industry, enable 'COI Co-op Prototype' if needed.
8. Load the *_CLIENT save name printed by the launcher.

Relay:
$relayBase

Both PCs must use the same Captain of Industry version and the same COI-Coop build.
The session token is obtained at launch time and is not stored in this ZIP.
No save is bundled in this ZIP: every session receives the exact host snapshot through the authenticated relay and verifies SHA256 before launching.

CoiCoop.dll SHA256:
$dllHash
"@
Set-Content -LiteralPath (Join-Path $stageRoot "README.txt") -Value $readme -Encoding UTF8

$manifest = @"
COI-Coop internet friend kit
Created: $(Get-Date -Format o)
Relay=$relayBase
Save payload=none; synchronized snapshot is downloaded at join time
CoiCoop.dll SHA256=$dllHash
"@
Set-Content -LiteralPath (Join-Path $stageRoot "SHA256.txt") -Value $manifest -Encoding ASCII

Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host ""
Write-Host "INTERNET FRIEND KIT READY" -ForegroundColor Green
Write-Host "ZIP:   $zipPath"
Write-Host "Relay: $relayBase"
Write-Host "Mod SHA256: $dllHash"
Write-Host ""
Write-Host "No save is bundled. Send this ZIP once; future sessions only need a session code."
