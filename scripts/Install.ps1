#requires -Version 5.1
[CmdletBinding()]
param([string]$GameDirectory, [switch]$PlanOnly, [switch]$ValidateOnly, [switch]$SkipDependencies)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$gameRelative = 'lab\fleet-operations\game'
$dataRelative = 'lab\fleet-operations\userdata'
$logsRelative = 'lab\fleet-operations\logs'
$exeName = 'Ultimate Admiral Dreadnoughts.exe'
$dataName = 'Ultimate Admiral Dreadnoughts_Data'
$mlHash = '687B82605606E941CEFDC007B880B720922CC319BB70270064590D4038C3C0DB'

function Value($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -ne $p) { return $p.Value }
    return $null
}
function Inside([string]$Child, [string]$Parent) {
    $a = [IO.Path]::GetFullPath($Child).TrimEnd('\')
    $b = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    return $a.Equals($b,[StringComparison]::OrdinalIgnoreCase) -or $a.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase)
}
function NoLinks([string]$Path) {
    $p = [IO.Path]::GetFullPath($Path)
    while (![string]::IsNullOrWhiteSpace($p)) {
        if (Test-Path -LiteralPath $p) {
            if (((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $p" }
        }
        $parent = [IO.Path]::GetDirectoryName($p.TrimEnd('\'))
        if ($parent -eq $p) { break }
        $p = $parent
    }
}
function NoTreeLinks([string]$Path) {
    NoLinks $Path
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused in source tree: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}
function CheckHash([string]$Path, [string]$Expected, [string]$Algorithm = 'SHA256') {
    $length = if ($Algorithm -eq 'SHA512') { 128 } else { 64 }
    if ($Expected -notmatch ('^[A-Fa-f0-9]{'+$length+'}$')) { throw "Invalid $Algorithm hash in marker: $Path" }
    NoLinks $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ine $Expected) { throw "Hash mismatch: $Path" }
}
function CheckNative([string]$Directory, $Marker) {
    CheckHash (Join-Path $Directory 'GameAssembly.dll') ([string](Value $Marker 'GameAssemblyHash'))
    CheckHash (Join-Path $Directory 'UnityPlayer.dll') ([string](Value $Marker 'UnityPlayerHash'))
    CheckHash (Join-Path $Directory ($dataName+'\il2cpp_data\Metadata\global-metadata.dat')) ([string](Value $Marker 'MetadataHash'))
}
function NoGame {
    if (@(Get-Process -Name 'Ultimate Admiral Dreadnoughts' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'A game is running. Close it normally; this installer never terminates processes.' }
}
function UnzipSafe([string]$Archive, [string]$Destination, [bool]$Loader) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    NoLinks $Destination
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if ($zip.Entries.Count -gt 10000) { throw 'Dependency archive contains too many entries.' }
        $total = [long]0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\','/')
            $total += $entry.Length
            if ($total -gt 2GB -or $entry.Length -gt 1GB) { throw 'Dependency archive exceeds extraction size limit.' }
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or ($name.Split('/') -contains '..') -or ($name.Split('/') -contains '.')) { throw "Unsafe archive path: $name" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or (($entry.ExternalAttributes -band 0x400) -ne 0)) { throw "Archive link refused: $name" }
            if ($Loader -and $name -ne 'version.dll' -and !$name.StartsWith('MelonLoader/',[StringComparison]::Ordinal)) { throw "Unexpected MelonLoader archive entry: $name" }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (!(Inside $target $Destination) -or $target -eq $Destination) { throw "Archive escaped destination: $name" }
            NoLinks $target
            if ($name.EndsWith('/')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Dependency($Spec, [string]$Kind) {
    if ($null -eq $Spec) { throw "Missing Dependencies.$Kind in package marker." }
    $url = [string](Value $Spec 'Url')
    $name = [string](Value $Spec 'FileName')
    if ($name -notmatch '^[A-Za-z0-9_.-]+\.zip$') { throw "Unsafe dependency cache filename: $name" }
    $uri = New-Object Uri($url)
    if ($uri.Scheme -ne 'https') { throw 'Dependency URLs must use HTTPS.' }
    if ($Kind -eq 'MelonLoader') {
        if ($url -cne 'https://github.com/LavaGang/MelonLoader/releases/download/v0.6.6/MelonLoader.x64.zip') { throw 'Only the pinned official MelonLoader 0.6.6 asset is allowed.' }
        $expected = [string](Value $Spec 'Sha256')
        if ($expected -ine $mlHash) { throw 'MelonLoader SHA256 differs from the pinned 0.6.6 archive.' }
        $algorithm = 'SHA256'
    } else {
        if ($uri.Host -notin @('builds.dotnet.microsoft.com','download.visualstudio.microsoft.com','dotnetcli.azureedge.net')) { throw 'Only official Microsoft .NET dependency hosts are allowed.' }
        $expected = [string](Value $Spec 'Sha512')
        if ($expected -notmatch '^[A-Fa-f0-9]{128}$') { throw 'DotNet dependency requires its official SHA512.' }
        $algorithm = 'SHA512'
    }
    $cache = Join-Path $package 'dependencies\cache'
    NoLinks $cache
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $archive = Join-Path $cache $name
    if (!(Test-Path -LiteralPath $archive)) {
        $partial = $archive + '.downloading'
        NoLinks $partial
        if (Test-Path -LiteralPath $partial) { throw "An earlier partial download is preserved; inspect it before retrying: $partial" }
        Write-Host "Downloading official dependency: $url"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($null -ne $curl) {
            $arguments = @('--location','--fail','--silent','--show-error','--connect-timeout','15','--max-time','300','--output',$partial,$url)
            & $curl.Source @arguments
            if ($LASTEXITCODE -ne 0) { throw "Dependency download failed; partial file preserved: $partial" }
        } else { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $partial -TimeoutSec 180 }
        CheckHash $partial $expected $algorithm
        Move-Item -LiteralPath $partial -Destination $archive
    }
    CheckHash $archive $expected $algorithm
    return $archive
}

try {
    NoLinks $package
    $markerPath = Join-Path $package 'fleetops-package.json'
    NoLinks $markerPath
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $schema = Value $marker 'SchemaVersion'; if ($null -eq $schema) { $schema = Value $marker 'Schema' }
    if ((Value $marker 'PackageId') -cne 'uad-fleet-operations' -or $schema -ne 1 -or (Value $marker 'ExportVersion') -cne '0.0.8.1-export-r1') { throw 'Unsupported fleetops-package.json identity/schema/version.' }
    foreach ($pair in @(@('GameRelativePath',$gameRelative),@('DataRelativePath',$dataRelative),@('LogsRelativePath',$logsRelative))) {
        $declared = [string](Value $marker $pair[0])
        if ($declared -and $declared.Replace('/','\').TrimEnd('\') -cne $pair[1]) { throw "Unexpected package path: $($pair[0])" }
    }
    if ($package -match '[^\x20-\x7E]') { Write-Warning 'An absolute ASCII package path is recommended, for example C:\UAD-FleetOps. Do not use junctions.' }
    NoGame
    if ([string]::IsNullOrWhiteSpace($GameDirectory)) { $GameDirectory = Read-Host 'Select your own Steam game folder (Steam > Manage > Browse local files); no game files are downloaded' }
    if (!(Test-Path -LiteralPath $GameDirectory -PathType Container)) { throw 'GameDirectory does not exist.' }
    $source = [IO.Path]::GetFullPath((Get-Item -LiteralPath $GameDirectory).FullName).TrimEnd('\')
    if ((Inside $source $package) -or (Inside $package $source)) { throw 'Source and package directories overlap; neither may be inside the other.' }
    NoLinks $source
    $target = Join-Path $package $gameRelative
    $stage = $target + '.installing'
    foreach ($path in @($target,$stage,(Join-Path $package $dataRelative),(Join-Path $package $logsRelative))) { NoLinks $path }
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $stage)) { throw 'Target game or game.installing already exists. Nothing is overwritten or deleted.' }
    $manifestPath = Join-Path $package 'lab\fleet-operations\install-manifest.json'
    NoLinks $manifestPath
    if (Test-Path -LiteralPath $manifestPath) { throw 'An installation manifest already exists; previous state is preserved.' }
    $allowed = @('Ultimate Admiral Dreadnoughts.exe','UnityPlayer.dll','GameAssembly.dll','UnityCrashHandler64.exe','baselib.dll','steam_appid.txt','version.xml','concrt140.dll','msvcp140.dll','vcamp140.dll','vccorlib140.dll','vcomp140.dll','vcruntime140.dll','vcruntime140_1.dll','steam_api64.dll')
    $rootFiles = @(Value $marker 'RootFiles')
    if ($rootFiles.Count -eq 0) { throw 'RootFiles whitelist is empty.' }
    $seen = @{}
    foreach ($name in $rootFiles) {
        if ($name -isnot [string] -or $name -notin $allowed -or $seen.ContainsKey($name)) { throw "RootFiles whitelist contains an unsupported or duplicate filename: $name" }
        $seen[$name] = $true
        $path = Join-Path $source $name
        NoLinks $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Whitelisted source file missing: $name" }
    }
    foreach ($required in @($exeName,'UnityPlayer.dll','GameAssembly.dll')) { if ($required -notin $rootFiles) { throw "RootFiles must include $required" } }
    $sourceData = Join-Path $source $dataName
    if (!(Test-Path -LiteralPath $sourceData -PathType Container)) { throw 'Original game Data directory is missing.' }
    NoTreeLinks $sourceData
    CheckNative $source $marker
    $payloadHashes = Value $marker 'PayloadHashes'
    foreach ($name in @('FleetOpsLabIsolation.dll','FleetOperations.dll')) {
        $relative = 'payload/Mods/'+$name
        CheckHash (Join-Path $package $relative) ([string](Value $payloadHashes $relative))
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath (Join-Path $package 'payload\Mods') -Force)
    if ($payloadFiles.Count -ne 2 -or @($payloadFiles | Where-Object { $_.PSIsContainer -or $_.Name -notin @('FleetOpsLabIsolation.dll','FleetOperations.dll') }).Count -gt 0) { throw 'payload/Mods must contain exactly the two reviewed DLLs.' }
    $dataFiles = @(Get-ChildItem -LiteralPath $sourceData -File -Recurse -Force | Where-Object { $_.Name -notmatch '^(dxgi|d3d11|d3d12|nvngx[^.]*|dlss[^.]*)\.dll$' -and $_.FullName -notmatch '[\\/]reshade[^\\/]*([\\/]|$)' })
    $dataDirectories = @(Get-ChildItem -LiteralPath $sourceData -Directory -Recurse -Force | Where-Object { $_.FullName -notmatch '[\\/]reshade[^\\/]*([\\/]|$)' })
    $copyBytes = [long]0
    foreach ($name in $rootFiles) { $copyBytes += (Get-Item -LiteralPath (Join-Path $source $name)).Length }
    foreach ($file in $dataFiles) { $copyBytes += $file.Length }
    if ($PlanOnly -or $ValidateOnly) {
        [pscustomobject]@{Status='PLAN_ONLY_NOT_INSTALLED';Package=$package;Source=$source;Target=$target;RootFiles=$rootFiles;DataFiles=$dataFiles.Count;CopyBytes=$copyBytes;DependenciesDownloaded=$false;GameStarted=$false}
        exit 0
    }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($package))
    if ($drive.AvailableFreeSpace -lt ($copyBytes+512MB)) { throw 'Insufficient free space for an independent copy plus dependency workspace.' }
    NoGame
    $mlArchive = $null; $netArchive = $null
    if (!$SkipDependencies) {
        $dependencies = Value $marker 'Dependencies'
        $mlArchive = Dependency (Value $dependencies 'MelonLoader') 'MelonLoader'
        $netArchive = Dependency (Value $dependencies 'DotNet') 'DotNet'
        $runtime = Join-Path $package 'runtime\dotnet'
        NoLinks $runtime
        if (Test-Path -LiteralPath $runtime) { throw 'runtime/dotnet already exists; this installer does not overwrite it.' }
        $runtimeStage = $runtime + '.installing'
        NoLinks $runtimeStage
        if (Test-Path -LiteralPath $runtimeStage) { throw 'runtime/dotnet.installing already exists and is preserved.' }
        [IO.Directory]::CreateDirectory($runtimeStage) | Out-Null
        UnzipSafe $netArchive $runtimeStage $false
        foreach ($file in @('dotnet.exe','host\fxr\6.0.36\hostfxr.dll','shared\Microsoft.NETCore.App\6.0.36\System.Private.CoreLib.dll','shared\Microsoft.NETCore.App\6.0.36\hostpolicy.dll','LICENSE.txt','ThirdPartyNotices.txt')) {
            if (!(Test-Path -LiteralPath (Join-Path $runtimeStage $file) -PathType Leaf)) { throw "Required .NET Core 6.0.36 runtime file missing: $file" }
        }
        Rename-Item -LiteralPath $runtimeStage -NewName 'dotnet'
    }
    NoGame
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($name in $rootFiles) {
        NoGame; $path=Join-Path $source $name; NoLinks $path
        Copy-Item -LiteralPath $path -Destination (Join-Path $stage $name)
    }
    [IO.Directory]::CreateDirectory((Join-Path $stage $dataName)) | Out-Null
    foreach ($directory in $dataDirectories) {
        NoGame; NoLinks $directory.FullName
        $relative = $directory.FullName.Substring($sourceData.Length+1)
        [IO.Directory]::CreateDirectory((Join-Path (Join-Path $stage $dataName) $relative)) | Out-Null
    }
    foreach ($file in $dataFiles) {
        NoGame; NoLinks $file.FullName
        $relative = $file.FullName.Substring($sourceData.Length+1)
        $destination = Join-Path (Join-Path $stage $dataName) $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    CheckNative $stage $marker
    if (!$SkipDependencies) { UnzipSafe $mlArchive $stage $true }
    foreach ($directory in @('Mods','Plugins','UserLibs','UserData')) { [IO.Directory]::CreateDirectory((Join-Path $stage $directory)) | Out-Null }
    foreach ($name in @('FleetOpsLabIsolation.dll','FleetOperations.dll')) {
        $relative = 'payload/Mods/'+$name
        Copy-Item -LiteralPath (Join-Path $package $relative) -Destination (Join-Path $stage ('Mods\'+$name))
        CheckHash (Join-Path $stage ('Mods\'+$name)) ([string](Value $payloadHashes $relative))
    }
    NoGame; NoTreeLinks $stage; CheckNative $stage $marker
    Rename-Item -LiteralPath $stage -NewName 'game'
    foreach ($directory in @($dataRelative,($dataRelative+'\Designs'),$logsRelative)) { NoLinks (Join-Path $package $directory); [IO.Directory]::CreateDirectory((Join-Path $package $directory)) | Out-Null }
    $status = if ($SkipDependencies) { 'INCOMPLETE_SKIP_DEPENDENCIES' } else { 'INSTALLED' }
    $receipt = [ordered]@{PackageId='uad-fleet-operations';SchemaVersion=1;ExportVersion='0.0.8.1-export-r1';Status=$status;Ready=(!$SkipDependencies);InstalledUtc=[DateTime]::UtcNow.ToString('o');PackageMarkerHash=(Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash;SourceDirectory=$source;GameRelativePath=$gameRelative;DataRelativePath=$dataRelative;PayloadHashes=$payloadHashes;GameAssemblyHash=(Value $marker 'GameAssemblyHash');MetadataHash=(Value $marker 'MetadataHash');UnityPlayerHash=(Value $marker 'UnityPlayerHash');CopyMethod='Independent Copy-Item files; no hardlinks';GameStarted=$false;RuntimeAcceptance='NOT_RUN'}
    $json = $receipt | ConvertTo-Json -Depth 8
    $stream = [IO.File]::Open($manifestPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    Write-Host $status
    if ($SkipDependencies) { Write-Warning 'OFFLINE STRUCTURE TEST ONLY: dependencies skipped; this is not a runnable or accepted installation.' }
    else { Write-Host 'Independent installation prepared. First MelonLoader launch needs network access to generate your own interop assemblies. No game has been started.' }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
