param(
    [string]$CoiRoot = $env:COI_ROOT,
    [string]$OutputPath = (Join-Path $PSScriptRoot "ramp-api.txt")
)

$ErrorActionPreference = "Stop"

function Test-CoiRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return Test-Path (Join-Path $Path "Captain of Industry_Data\Managed\Mafi.Unity.dll")
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

if (-not (Test-CoiRoot $CoiRoot)) { $CoiRoot = Find-CoiRoot }
$managed = Join-Path $CoiRoot "Captain of Industry_Data\Managed"

$assemblies = New-Object System.Collections.Generic.List[System.Reflection.Assembly]
foreach ($file in (Get-ChildItem $managed -Filter "*.dll" | Sort-Object Name)) {
    try {
        $asm = [System.Reflection.Assembly]::LoadFrom($file.FullName)
        if ($asm -ne $null) { $assemblies.Add($asm) }
    }
    catch { }
}

function Format-TypeName([Type]$Type) {
    if ($Type -eq $null) { return "<null>" }
    if ($Type.IsByRef) { return "$(Format-TypeName $Type.GetElementType())&" }
    if ($Type.IsArray) { return "$(Format-TypeName $Type.GetElementType())[]" }
    if ($Type.IsGenericType) {
        $name = $Type.GetGenericTypeDefinition().FullName
        $tick = $name.IndexOf('`')
        if ($tick -ge 0) { $name = $name.Substring(0, $tick) }
        $args = @($Type.GetGenericArguments() | ForEach-Object { Format-TypeName $_ })
        return "$name<$($args -join ', ')>"
    }
    return $Type.FullName
}

$allTypes = New-Object System.Collections.Generic.List[Type]
foreach ($asm in $assemblies) {
    if (-not $asm.GetName().Name.StartsWith("Mafi", [System.StringComparison]::Ordinal)) { continue }
    try {
        foreach ($type in $asm.GetTypes()) {
            if ($type -ne $null) { $allTypes.Add($type) }
        }
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        foreach ($type in $_.Exception.Types) {
            if ($type -ne $null) { $allTypes.Add($type) }
        }
    }
    catch { }
}

$matches = @($allTypes | Where-Object {
    $n = $_.FullName
    $n -and (
        $n.IndexOf("Ramp", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $n.IndexOf("Slope", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    )
} | Sort-Object FullName -Unique)

$binding = [System.Reflection.BindingFlags]::Instance -bor
           [System.Reflection.BindingFlags]::Static -bor
           [System.Reflection.BindingFlags]::Public -bor
           [System.Reflection.BindingFlags]::NonPublic -bor
           [System.Reflection.BindingFlags]::DeclaredOnly
$ctorBinding = [System.Reflection.BindingFlags]::Instance -bor
               [System.Reflection.BindingFlags]::Public -bor
               [System.Reflection.BindingFlags]::NonPublic

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("Captain of Industry ramp/slope placement API metadata")
$lines.Add("Game root: $CoiRoot")
$lines.Add("Generated: $(Get-Date -Format o)")
$lines.Add("Matched types: $($matches.Count)")
$lines.Add("")

foreach ($type in $matches) {
    $lines.Add("================================================================================")
    $lines.Add("TYPE: $($type.FullName)")
    $lines.Add("Assembly: $($type.Assembly.GetName().Name) $($type.Assembly.GetName().Version)")
    $lines.Add("Base: $(Format-TypeName $type.BaseType)")

    $lines.Add("")
    $lines.Add("CONSTRUCTORS:")
    foreach ($ctor in @($type.GetConstructors($ctorBinding) | Sort-Object MetadataToken)) {
        $parameters = @($ctor.GetParameters() | ForEach-Object { "$(Format-TypeName $_.ParameterType) $($_.Name)" })
        $lines.Add("  .ctor($($parameters -join ', '))")
    }

    $lines.Add("")
    $lines.Add("FIELDS:")
    foreach ($field in @($type.GetFields($binding) | Sort-Object Name)) {
        $scope = if ($field.IsStatic) { "static" } else { "instance" }
        $lines.Add("  [$scope] $(Format-TypeName $field.FieldType) $($field.Name)")
    }

    $lines.Add("PROPERTIES:")
    foreach ($property in @($type.GetProperties($binding) | Sort-Object Name)) {
        $getter = if ($property.GetGetMethod($true)) { "get" } else { "" }
        $setter = if ($property.GetSetMethod($true)) { "set" } else { "" }
        $lines.Add("  $(Format-TypeName $property.PropertyType) $($property.Name) {$getter;$setter}")
    }

    $lines.Add("METHODS:")
    foreach ($method in @($type.GetMethods($binding) | Where-Object { -not $_.IsSpecialName } | Sort-Object Name, MetadataToken)) {
        $parameters = @($method.GetParameters() | ForEach-Object { "$(Format-TypeName $_.ParameterType) $($_.Name)" })
        $scope = if ($method.IsStatic) { "static " } else { "" }
        $lines.Add("  $scope$(Format-TypeName $method.ReturnType) $($method.Name)($($parameters -join ', '))")
    }
    $lines.Add("")
}

$lines | Set-Content -Path $OutputPath -Encoding UTF8
try { ($lines -join [Environment]::NewLine) | Set-Clipboard } catch { }

Write-Host "COI_ROOT: $CoiRoot"
Write-Host "Ramp/slope API metadata written to: $OutputPath"
Write-Host "The output was also copied to the clipboard when possible."
