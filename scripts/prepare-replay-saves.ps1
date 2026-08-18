param(
    [string]$SourceSaveName = "COOP_REPLAY_TEST"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$saveRoot = Join-Path $env:APPDATA "Captain of Industry\Saves"
if (-not (Test-Path $saveRoot)) {
    throw "Captain of Industry save directory was not found: $saveRoot"
}

$allSaves = @(Get-ChildItem $saveRoot -File -Recurse -Filter "*.save" | Sort-Object LastWriteTime -Descending)
if ($allSaves.Count -eq 0) {
    throw "No .save files were found under: $saveRoot"
}

$source = $null

# Accept a direct file path for troubleshooting/power-user use.
if (Test-Path -LiteralPath $SourceSaveName -PathType Leaf) {
    $candidate = Get-Item -LiteralPath $SourceSaveName
    if (-not $candidate.Name.EndsWith(".save", [StringComparison]::OrdinalIgnoreCase)) {
        throw "The selected file is not a .save file: $($candidate.FullName)"
    }
    $source = $candidate
}
else {
    $leaf = $SourceSaveName
    if (-not $leaf.EndsWith(".save", [StringComparison]::OrdinalIgnoreCase)) {
        $leaf += ".save"
    }

    $exactMatches = @($allSaves | Where-Object {
        [string]::Equals($_.Name, $leaf, [StringComparison]::OrdinalIgnoreCase)
    })

    if ($exactMatches.Count -eq 1) {
        $source = $exactMatches[0]
    }
    elseif ($exactMatches.Count -gt 1) {
        Write-Host "More than one save named '$leaf' was found:" -ForegroundColor Yellow
        $exactMatches | ForEach-Object { Write-Host "  $($_.FullName)" }
        throw "Pass a full save path so the intended source is unambiguous."
    }
    else {
        $needle = [IO.Path]::GetFileNameWithoutExtension($SourceSaveName)
        $partialMatches = @($allSaves | Where-Object {
            $_.BaseName.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })

        if ($partialMatches.Count -eq 1) {
            $source = $partialMatches[0]
            Write-Host "Exact save '$leaf' was not found; using unique partial match:" -ForegroundColor Yellow
            Write-Host "  $($source.FullName)"
        }
        elseif ($partialMatches.Count -gt 1) {
            Write-Host "Exact save '$leaf' was not found. Partial name matched multiple saves:" -ForegroundColor Yellow
            $partialMatches | Select-Object -First 25 | ForEach-Object {
                Write-Host ("  {0}  [{1}]" -f $_.FullName, $_.LastWriteTime)
            }
            throw "Re-run with a more specific save name or a full file path."
        }
        else {
            Write-Host "Save '$leaf' was not found." -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Most recent .save files under ${saveRoot}:" -ForegroundColor Cyan
            $allSaves | Select-Object -First 25 | ForEach-Object {
                $relative = $_.FullName.Substring($saveRoot.Length).TrimStart('\')
                Write-Host ("  {0}  [{1}]" -f $relative, $_.LastWriteTime)
            }
            Write-Host ""
            throw "Re-run with one of the filenames shown above (or pass its full path)."
        }
    }
}

$directory = $source.DirectoryName
$baseName = [IO.Path]::GetFileNameWithoutExtension($source.Name)
$hostPath = Join-Path $directory ($baseName + "_HOST.save")
$clientPath = Join-Path $directory ($baseName + "_CLIENT.save")

Write-Host "Source: $($source.FullName)"
Write-Host "Creating two independent byte-identical starting saves..."

Copy-Item -LiteralPath $source.FullName -Destination $hostPath -Force
Copy-Item -LiteralPath $source.FullName -Destination $clientPath -Force

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source.FullName).Hash
$hostHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $hostPath).Hash
$clientHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $clientPath).Hash

if ($sourceHash -ne $hostHash -or $sourceHash -ne $clientHash) {
    throw "Replay save copies were created but their SHA-256 hashes do not match the source."
}

Write-Host ""
Write-Host "REPLAY SAVES READY" -ForegroundColor Green
Write-Host "Host:   $hostPath"
Write-Host "Client: $clientPath"
Write-Host "SHA256: $sourceHash"
Write-Host ""
Write-Host "Load '${baseName}_HOST' in the HOST game window."
Write-Host "Load '${baseName}_CLIENT' in the CLIENT game window."
Write-Host "Do not save either test world after the replay experiment."
