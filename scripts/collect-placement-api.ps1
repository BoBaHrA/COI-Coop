param(
    [string]$CoiRoot = $env:COI_ROOT,
    [string]$OutputPath = (Join-Path $PSScriptRoot "placement-api.txt")
)

$ErrorActionPreference = "Stop"

function Test-CoiRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return Test-Path (Join-Path $Path "Captain of Industry_Data\Managed\Mafi.Core.dll")
}

function Find-CoiRoot {
    $candidates = @()
    if ($env:COI_ROOT) { $candidates += $env:COI_ROOT }
    $candidates += @(
        "C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry",
        "C:\Program Files\Steam\steamapps\common\Captain of Industry",
        "D:\SteamLibrary\steamapps\common\Captain of Industry",
        "E:\SteamLibrary\steamapps\common\Captain of Industry"
    )

    foreach ($candidate in $candidates) {
        if (Test-CoiRoot $candidate) { return $candidate }
    }

    throw "Could not find Captain of Industry. Set COI_ROOT or pass -CoiRoot."
}

if (-not (Test-CoiRoot $CoiRoot)) {
    $CoiRoot = Find-CoiRoot
}

$managed = Join-Path $CoiRoot "Captain of Industry_Data\Managed"
Write-Host "COI_ROOT: $CoiRoot"
Write-Host "Managed:  $managed"

# Load Unity/Mafi assemblies into this temporary PowerShell process only. Failures
# are tolerated because many optional Unity modules are unrelated to our targets.
$assemblies = New-Object System.Collections.Generic.List[System.Reflection.Assembly]
$preferred = Get-ChildItem $managed -Filter "*.dll" | Sort-Object Name
foreach ($file in $preferred) {
    try {
        $asm = [System.Reflection.Assembly]::LoadFrom($file.FullName)
        if ($asm -ne $null) { $assemblies.Add($asm) }
    }
    catch {
        # Ignore optional/native/incompatible modules; report target lookup below.
    }
}

$targets = @(
    "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.StaticEntityMassPlacer",
    "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntitySlotPlacerHelper",
    "Mafi.Unity.Ui.Controllers.LayoutEntityPlacing.LayoutEntityToolbox",
    "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewManager",
    "Mafi.Unity.InputControl.Factory.LayoutEntityPreviewIcons",
    "Mafi.Unity.UiStatic.Controllers.LayoutEntityPlacing.LastUsedStaticEntityTransform"
)

$binding = [System.Reflection.BindingFlags]::Instance -bor
           [System.Reflection.BindingFlags]::Static -bor
           [System.Reflection.BindingFlags]::Public -bor
           [System.Reflection.BindingFlags]::NonPublic -bor
           [System.Reflection.BindingFlags]::DeclaredOnly

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Captain of Industry placement API metadata")
$lines.Add("Game root: $CoiRoot")
$lines.Add("Generated: $(Get-Date -Format o)")
$lines.Add("")

function Format-TypeName([Type]$Type) {
    if ($Type -eq $null) { return "<null>" }
    if ($Type.IsGenericType) {
        $name = $Type.GetGenericTypeDefinition().FullName
        $tick = $name.IndexOf('`')
        if ($tick -ge 0) { $name = $name.Substring(0, $tick) }
        $args = @($Type.GetGenericArguments() | ForEach-Object { Format-TypeName $_ })
        return "$name<$($args -join ', ')>"
    }
    if ($Type.IsArray) { return "$(Format-TypeName $Type.GetElementType())[]" }
    return $Type.FullName
}

foreach ($target in $targets) {
    $type = $null
    foreach ($asm in $assemblies) {
        try {
            $type = $asm.GetType($target, $false)
            if ($type -ne $null) { break }
        }
        catch { }
    }

    $lines.Add("================================================================================")
    $lines.Add("TYPE: $target")
    if ($type -eq $null) {
        $lines.Add("NOT FOUND")
        $lines.Add("")
        continue
    }

    $lines.Add("Assembly: $($type.Assembly.GetName().Name) $($type.Assembly.GetName().Version)")
    $lines.Add("Base: $(Format-TypeName $type.BaseType)")

    for ($current = $type; $current -ne $null -and $current -ne [object]; $current = $current.BaseType) {
        $lines.Add("")
        $lines.Add("-- DECLARED ON $($current.FullName) --")

        $fields = @($current.GetFields($binding) | Sort-Object Name)
        $lines.Add("FIELDS:")
        foreach ($field in $fields) {
            $scope = if ($field.IsStatic) { "static" } else { "instance" }
            $lines.Add("  [$scope] $(Format-TypeName $field.FieldType) $($field.Name)")
        }

        $properties = @($current.GetProperties($binding) | Sort-Object Name)
        $lines.Add("PROPERTIES:")
        foreach ($property in $properties) {
            $index = @($property.GetIndexParameters() | ForEach-Object { "$(Format-TypeName $_.ParameterType) $($_.Name)" })
            $suffix = if ($index.Count -gt 0) { "[$($index -join ', ')]" } else { "" }
            $getter = if ($property.GetGetMethod($true)) { "get" } else { "" }
            $setter = if ($property.GetSetMethod($true)) { "set" } else { "" }
            $lines.Add("  $(Format-TypeName $property.PropertyType) $($property.Name)$suffix {$getter;$setter}")
        }

        $methods = @($current.GetMethods($binding) |
            Where-Object { -not $_.IsSpecialName } |
            Sort-Object Name, MetadataToken)
        $lines.Add("METHODS:")
        foreach ($method in $methods) {
            $parameters = @($method.GetParameters() | ForEach-Object {
                "$(Format-TypeName $_.ParameterType) $($_.Name)"
            })
            $generic = if ($method.IsGenericMethodDefinition) {
                "<$((@($method.GetGenericArguments() | ForEach-Object Name)) -join ', ')>"
            } else { "" }
            $scope = if ($method.IsStatic) { "static " } else { "" }
            $lines.Add("  $scope$(Format-TypeName $method.ReturnType) $($method.Name)$generic($($parameters -join ', '))")
        }
    }

    $lines.Add("")
}

$lines | Set-Content -Path $OutputPath -Encoding UTF8
try { ($lines -join [Environment]::NewLine) | Set-Clipboard } catch { }

Write-Host ""
Write-Host "Placement API metadata written to: $OutputPath"
Write-Host "The output was also copied to the clipboard when possible."
