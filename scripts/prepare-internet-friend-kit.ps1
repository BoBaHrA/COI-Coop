param(
    [string]$SourceSaveName = "COOP_LAN_BASE",

    [Parameter(Mandatory = $true)]
    [string]$RelayUrl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
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

if (-not (Test-Path $saveRoot)) {
    throw "Captain of Industry save directory was not found: $saveRoot"
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

$baseName = [IO.Path]::GetFileNameWithoutExtension($SourceSaveName)
if ($baseName.EndsWith("_CLIENT", [StringComparison]::OrdinalIgnoreCase)) {
    $clientLeaf = $baseName + ".save"
}
else {
    $clientLeaf = $baseName + "_CLIENT.save"
}

$clientMatches = @(Get-ChildItem $saveRoot -File -Recurse -Filter $clientLeaf)
if ($clientMatches.Count -eq 0) {
    throw "Client save '$clientLeaf' was not found under: $saveRoot. Run scripts\prepare-replay-saves.bat first."
}
if ($clientMatches.Count -gt 1) {
    Write-Host "More than one client save named '$clientLeaf' was found:" -ForegroundColor Yellow
    $clientMatches | ForEach-Object { Write-Host "  $($_.FullName)" }
    throw "Rename/remove duplicate test saves or pass a more specific source name."
}
$clientSave = $clientMatches[0]

$relativeSaveDirectory = $clientSave.DirectoryName.Substring($saveRoot.Length).TrimStart('\')
$relativeSavePath = if ([string]::IsNullOrWhiteSpace($relativeSaveDirectory)) {
    $clientSave.Name
}
else {
    Join-Path $relativeSaveDirectory $clientSave.Name
}

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

$stageScripts = Join-Path $stageRoot "scripts"
$stageMod = Join-Path $stageRoot "payload\CoiCoop"
$stageSavesRoot = Join-Path $stageRoot "payload\Saves"
$stageSavePath = Join-Path $stageSavesRoot $relativeSavePath
$stageSaveDirectory = Split-Path -Parent $stageSavePath
New-Item -ItemType Directory -Path $stageScripts -Force | Out-Null
New-Item -ItemType Directory -Path $stageMod -Force | Out-Null
New-Item -ItemType Directory -Path $stageSaveDirectory -Force | Out-Null

Copy-Item (Join-Path $PSScriptRoot "launch-coop.ps1") $stageScripts -Force
Copy-Item (Join-Path $PSScriptRoot "launch-internet.ps1") $stageScripts -Force
foreach ($name in $requiredModFiles) {
    Copy-Item (Join-Path $modRoot $name) (Join-Path $stageMod $name) -Force
}
Copy-Item $clientSave.FullName $stageSavePath -Force

$dllHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stageMod "CoiCoop.dll")).Hash
$saveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stageSavePath).Hash

$installer = @"
@echo off
setlocal
cd /d "%~dp0"

echo === COI-Coop INTERNET CLIENT installer ===
echo.

set "MODDST=%APPDATA%\Captain of Industry\Mods\CoiCoop"
set "SAVEROOT=%APPDATA%\Captain of Industry\Saves"

if not exist "%MODDST%" mkdir "%MODDST%"
if not exist "%SAVEROOT%" mkdir "%SAVEROOT%"

copy /Y "payload\CoiCoop\CoiCoop.dll" "%MODDST%\CoiCoop.dll" >nul || goto :fail
copy /Y "payload\CoiCoop\manifest.json" "%MODDST%\manifest.json" >nul || goto :fail
copy /Y "payload\CoiCoop\config.json" "%MODDST%\config.json" >nul || goto :fail
xcopy "payload\Saves\*" "%SAVEROOT%\" /E /I /Y >nul || goto :fail

echo Mod installed to: %MODDST%
echo Save payload copied under: %SAVEROOT%
echo Relay: $relayBase
echo.
echo Enter the SESSION CODE received from the host.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\launch-internet.ps1" -Mode Client -RelayUrl "$relayBase"
exit /b %ERRORLEVEL%

:fail
echo.
echo INSTALL FAILED.
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
5. In Captain of Industry, enable 'COI Co-op Prototype' if needed.
6. Load '$($clientSave.BaseName)'.

Relay:
$relayBase

Both PCs must use the same Captain of Industry version and the same COI-Coop build.
The session token is obtained at launch time and is not stored in this ZIP.

Installed save relative path:
$relativeSavePath

CoiCoop.dll SHA256:
$dllHash

$($clientSave.Name) SHA256:
$saveHash
"@
Set-Content -LiteralPath (Join-Path $stageRoot "README.txt") -Value $readme -Encoding UTF8

$manifest = @"
COI-Coop internet friend kit
Created: $(Get-Date -Format o)
Relay=$relayBase
Save relative path=$relativeSavePath
CoiCoop.dll SHA256=$dllHash
$($clientSave.Name) SHA256=$saveHash
"@
Set-Content -LiteralPath (Join-Path $stageRoot "SHA256.txt") -Value $manifest -Encoding ASCII

Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host ""
Write-Host "INTERNET FRIEND KIT READY" -ForegroundColor Green
Write-Host "ZIP:   $zipPath"
Write-Host "Relay: $relayBase"
Write-Host "Save SHA256: $saveHash"
Write-Host "Mod  SHA256: $dllHash"
Write-Host ""
Write-Host "Send this ZIP to your friend. They only need the session code when you host."
