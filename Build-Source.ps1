#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameDirectory,
    [string]$SourceDirectory = '',
    [string]$OutputDirectory = '',
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
# Windows PowerShell 5.1 may evaluate parameter defaults before PSScriptRoot is populated.
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = Join-Path $scriptDirectory 'src' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $scriptDirectory 'out' }

function Resolve-FleetExportDirectory {
    param([string]$Path, [string]$Description)
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description directory does not exist: $Path"
    }
    return (Get-Item -LiteralPath $Path).FullName
}

function Assert-FleetExportFile {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    return (Get-Item -LiteralPath $Path).FullName
}

function Test-FleetExportInside {
    param([string]$Child, [string]$Parent)
    $a = [IO.Path]::GetFullPath($Child).TrimEnd([char[]]'\/')
    $b = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]'\/')
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith($b + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Write-FleetExportText {
    param([string]$Path, [string]$Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function New-FleetExportContext {
    param([string]$Output, [string]$Sdk, [string[]]$ReadOnlyRoots)
    $full = [IO.Path]::GetFullPath($Output)
    foreach ($root in $ReadOnlyRoots) {
        if (Test-FleetExportInside $full $root) { throw "OutputDirectory must be outside read-only source/game directories: $full" }
    }
    if (Test-Path -LiteralPath $Sdk -PathType Leaf) { $tool = (Get-Item -LiteralPath $Sdk).FullName }
    else {
        $command = Get-Command $Sdk -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $command) { throw 'An existing .NET 6 SDK is required. Supply -DotnetPath; this script does not download/install it.' }
        $tool = $command.Source
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
    foreach ($name in @('projects', 'bin', 'logs', 'dotnet-home', 'packages', 'tmp', 'http-cache', 'plugin-cache')) {
        New-Item -ItemType Directory -Path (Join-Path $full $name) -Force | Out-Null
    }
    $config = Join-Path $full 'NuGet.offline.config'
    Write-FleetExportText $config '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>'
    return [pscustomobject]@{ Output = $full; Dotnet = $tool; NuGetConfig = $config }
}

function Invoke-FleetExportContext {
    param($Context, [scriptblock]$Action)
    $saved = @{}
    $values = @{
        DOTNET_ROOT = (Split-Path -Parent $Context.Dotnet)
        DOTNET_MULTILEVEL_LOOKUP = '0'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        DOTNET_NOLOGO = 'true'
        MSBUILDDISABLENODEREUSE = '1'
        DOTNET_CLI_HOME = (Join-Path $Context.Output 'dotnet-home')
        NUGET_PACKAGES = (Join-Path $Context.Output 'packages')
        NUGET_HTTP_CACHE_PATH = (Join-Path $Context.Output 'http-cache')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $Context.Output 'plugin-cache')
        NUGET_SCRATCH = (Join-Path $Context.Output 'tmp')
        TEMP = (Join-Path $Context.Output 'tmp')
        TMP = (Join-Path $Context.Output 'tmp')
    }
    foreach ($key in $values.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $values[$key], 'Process')
    }
    Push-Location $Context.Output
    try { & $Action }
    finally {
        Pop-Location
        foreach ($key in $values.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
    }
}

function Invoke-FleetExportDotnet {
    param($Context, [string[]]$Arguments, [string]$Log)
    Add-Content -LiteralPath $Log -Value ('dotnet ' + ($Arguments -join ' ')) -Encoding UTF8
    # Tee-Object uses UTF-16 in Windows PowerShell 5.1; keep a single UTF-8 log encoding.
    & $Context.Dotnet @Arguments 2>&1 | ForEach-Object {
        $line = [string]$_
        Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
        Write-Host $line
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited $LASTEXITCODE. See $Log. No game files were changed." }
}

function Assert-FleetExportSdk {
    param($Context)
    $sdkList = & $Context.Dotnet --list-sdks 2>&1
    if ($LASTEXITCODE -ne 0 -or !(@($sdkList) -match '^6\.0\.')) {
        throw 'An installed .NET 6 SDK (including net6.0 reference packs) is required for this offline build. No packages will be fetched.'
    }
    Write-FleetExportText (Join-Path $Context.Output 'logs/sdk.txt') ($sdkList -join [Environment]::NewLine)
    $versions = @($sdkList | ForEach-Object {
        if ([string]$_ -match '^(6\.0\.\d+)\s') { [version]$Matches[1] }
    })
    $selected = ($versions | Sort-Object -Descending | Select-Object -First 1).ToString()
    Write-FleetExportText (Join-Path $Context.Output 'global.json') (@{ sdk = @{ version = $selected; rollForward = 'disable' } } | ConvertTo-Json -Depth 3)
}

function New-FleetExportProject {
    param($Context, [string]$Name, [string[]]$Sources, [string[]]$References = @(), [switch]$Executable)
    $directory = Join-Path $Context.Output ('projects/' + $Name)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $obj = [Security.SecurityElement]::Escape((Join-Path $directory 'obj') + [IO.Path]::DirectorySeparatorChar)
    $bin = [Security.SecurityElement]::Escape((Join-Path $Context.Output ('bin/' + $Name)) + [IO.Path]::DirectorySeparatorChar)
    $type = if ($Executable) { 'Exe' } else { 'Library' }
    $items = @()
    foreach ($source in $Sources) {
        $file = [Security.SecurityElement]::Escape((Assert-FleetExportFile $source))
        $items += "    <Compile Include=`"$file`" />"
    }
    foreach ($reference in $References) {
        $file = [Security.SecurityElement]::Escape((Assert-FleetExportFile $reference))
        $items += "    <Reference Include=`"$file`"><Private>false</Private></Reference>"
    }
    $project = Join-Path $directory ($Name + '.csproj')
    $xml = @"
<Project>
  <PropertyGroup>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
    <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <BaseIntermediateOutputPath>$obj</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$obj</MSBuildProjectExtensionsPath>
  </PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework><OutputType>$type</OutputType>
    <AssemblyName>$Name</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <UseSharedCompilation>false</UseSharedCompilation>
    <OutputPath>$bin</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>
  <ItemGroup>
$($items -join [Environment]::NewLine)
  </ItemGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
"@
    Write-FleetExportText $project $xml
    return $project
}

function Build-FleetExportProject {
    param($Context, [string]$Project, [string]$Name, [string]$BuildConfiguration)
    $log = Join-Path $Context.Output ('logs/' + $Name + '.build.log')
    Write-FleetExportText $log ''
    Invoke-FleetExportDotnet $Context @('restore', $Project, '--configfile', $Context.NuGetConfig, '--verbosity', 'minimal') $log
    Invoke-FleetExportDotnet $Context @('build', $Project, '--no-restore', '--configuration', $BuildConfiguration, '--verbosity', 'minimal') $log
    return (Assert-FleetExportFile (Join-Path $Context.Output ('bin/' + $Name + '/' + $Name + '.dll')))
}

function Invoke-FleetSourceBuild {
    param([string]$Game, [string]$Source, [string]$Output, [string]$Sdk, [string]$BuildConfiguration)
    $gameRoot = Resolve-FleetExportDirectory $Game 'GameDirectory'
    foreach ($relative in @('GameAssembly.dll', 'MelonLoader/net6/MelonLoader.dll', 'MelonLoader/net6/0Harmony.dll',
        'MelonLoader/net6/Il2CppInterop.Runtime.dll', 'MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll',
        'MelonLoader/Il2CppAssemblies/Il2Cppmscorlib.dll', 'MelonLoader/Il2CppAssemblies/UnityEngine.CoreModule.dll',
        'MelonLoader/Il2CppAssemblies/UnityEngine.IMGUIModule.dll')) {
        [void](Assert-FleetExportFile (Join-Path $gameRoot $relative))
    }
    $sourceRoot = Resolve-FleetExportDirectory $Source 'SourceDirectory'
    $context = New-FleetExportContext $Output $Sdk @($sourceRoot, $gameRoot)
    $runtimeFiles = @('FleetOperations.cs', 'FleetOpsCore.cs', 'FleetPointBridge.cs', 'FleetTorpedoOrders.cs',
        'FleetGunImpactGeometry.cs', 'FleetGunImpactBridge.cs', 'FleetDamageEvidence.cs', 'FleetGunVolley.cs',
        'FleetBarrelLaunch.cs', 'FleetBarrelLaunchGeometry.cs', 'FleetNativeSideGeometry.cs',
        'FleetGunDispersion.cs', 'FleetGunDispersionGeometry.cs', 'FleetDispersionOverlay.cs', 'FleetDispersionOverlayGeometry.cs',
        'FleetOpsPackagePaths.cs', 'FleetSalvoOverlay.cs', 'FleetSettings.cs', 'FleetLocalization.cs',
        'FleetTorpedoOverlay.cs', 'FleetFireSolution.cs', 'FleetFireSolutionGeometry.cs')
    $sources = @($runtimeFiles | ForEach-Object { Join-Path $sourceRoot $_ })
    $nativeRefs = @((Get-ChildItem -LiteralPath (Join-Path $gameRoot 'MelonLoader/Il2CppAssemblies') -Filter '*.dll' -File).FullName)
    $nativeRefs += @('MelonLoader.dll', '0Harmony.dll', 'Il2CppInterop.Runtime.dll') | ForEach-Object { Join-Path $gameRoot ('MelonLoader/net6/' + $_) }
    $runtime = New-FleetExportProject $context 'FleetOperations' $sources $nativeRefs
    $isolation = New-FleetExportProject $context 'FleetOpsLabIsolation' @((Join-Path $sourceRoot 'lab-infrastructure/FleetOpsLabIsolation.cs'),
        (Join-Path $sourceRoot 'FleetOpsPackagePaths.cs')) @((Join-Path $gameRoot 'MelonLoader/net6/MelonLoader.dll'))
    Invoke-FleetExportContext $context {
        Assert-FleetExportSdk $context
        $dlls = @(
            Build-FleetExportProject $context $runtime 'FleetOperations' $BuildConfiguration
            Build-FleetExportProject $context $isolation 'FleetOpsLabIsolation' $BuildConfiguration
        )
        $receipt = [pscustomobject]@{ utc = [DateTime]::UtcNow.ToString('o'); gameReferences = $gameRoot; source = $sourceRoot;
            outputs = @($dlls | ForEach-Object { [pscustomobject]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } });
            boundary = 'Build only. No installation, launch, runtime acceptance or isolation-path portability is implied.' }
        Write-FleetExportText (Join-Path $context.Output 'build-result.json') ($receipt | ConvertTo-Json -Depth 5)
        Write-Host ('BUILD PASS. Outputs and logs: ' + $context.Output)
    }
}

# Test-Source dot-sources these helpers; loading them never builds or writes anything.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-FleetSourceBuild $GameDirectory $SourceDirectory $OutputDirectory $DotnetPath $Configuration }
    catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
}
