#requires -Version 5.1
[CmdletBinding()]
param([switch]$ValidateOnly, [switch]$PlanOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
function Value($Object,[string]$Name) { if($null -eq $Object){return $null};$p=$Object.PSObject.Properties[$Name];if($null -ne $p){return $p.Value};return $null }
function NoLinks([string]$Path) {
    $p=[IO.Path]::GetFullPath($Path)
    while(![string]::IsNullOrWhiteSpace($p)) {
        if(Test-Path -LiteralPath $p) { if(((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Reparse point refused: $p"} }
        $parent=[IO.Path]::GetDirectoryName($p.TrimEnd('\'));if($parent -eq $p){break};$p=$parent
    }
}
function NoTreeLinks([string]$Path) {
    NoLinks $Path
    $pending=New-Object 'System.Collections.Generic.Stack[string]';$pending.Push($Path)
    while($pending.Count -gt 0) {
        foreach($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Reparse point refused: $($item.FullName)"}
            if($item.PSIsContainer){$pending.Push($item.FullName)}
        }
    }
}
function CheckHash([string]$Path,[string]$Expected) {
    if($Expected -notmatch '^[A-Fa-f0-9]{64}$'){throw "Invalid SHA256 in package marker: $Path"}
    NoLinks $Path
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw "Required file missing: $Path"}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected){throw "Hash mismatch: $Path"}
}
try {
    NoLinks $package
    $markerPath=Join-Path $package 'fleetops-package.json';NoLinks $markerPath
    $marker=Get-Content -LiteralPath $markerPath -Raw|ConvertFrom-Json
    $schema=Value $marker 'SchemaVersion';if($null -eq $schema){$schema=Value $marker 'Schema'}
    if((Value $marker 'PackageId') -cne 'uad-fleet-operations' -or $schema -ne 1 -or (Value $marker 'ExportVersion') -cne '0.0.8.1-export-r1'){throw 'Unsupported fleetops-package.json identity/schema/version.'}
    foreach($pair in @(@('GameRelativePath','lab\fleet-operations\game'),@('DataRelativePath','lab\fleet-operations\userdata'),@('LogsRelativePath','lab\fleet-operations\logs'))) {
        $declared=[string](Value $marker $pair[0]);if($declared -and $declared.Replace('/','\').TrimEnd('\') -cne $pair[1]){throw "Unexpected package path: $($pair[0])"}
    }
    $manifestPath=Join-Path $package 'lab\fleet-operations\install-manifest.json';NoLinks $manifestPath
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    if((Value $manifest 'PackageId') -cne 'uad-fleet-operations' -or (Value $manifest 'ExportVersion') -cne '0.0.8.1-export-r1' -or (Value $manifest 'SchemaVersion') -ne 1){throw 'Installation manifest identity does not match this export.'}
    if((Value $manifest 'Ready') -ne $true -or (Value $manifest 'Status') -cne 'INSTALLED'){throw 'Installation is INCOMPLETE. Offline/SkipDependencies fixtures are not runnable installations.'}
    CheckHash $markerPath ([string](Value $manifest 'PackageMarkerHash'))
    $game=Join-Path $package 'lab\fleet-operations\game'
    $data=Join-Path $package 'lab\fleet-operations\userdata'
    $logs=Join-Path $package 'lab\fleet-operations\logs'
    $runtime=Join-Path $package 'runtime\dotnet'
    $exe=Join-Path $game 'Ultimate Admiral Dreadnoughts.exe'
    foreach($path in @($game,$data,(Join-Path $data 'Designs'),$logs,$runtime)) {
        NoLinks $path
        if(!(Test-Path -LiteralPath $path -PathType Container)){throw "Independent installation directory is missing: $path"}
    }
    NoTreeLinks $game;NoTreeLinks $data;NoTreeLinks $runtime
    foreach($file in @($exe,(Join-Path $game 'version.dll'),(Join-Path $game 'MelonLoader\net6\MelonLoader.dll'),(Join-Path $runtime 'dotnet.exe'),(Join-Path $runtime 'host\fxr\6.0.36\hostfxr.dll'),(Join-Path $runtime 'shared\Microsoft.NETCore.App\6.0.36\System.Private.CoreLib.dll'),(Join-Path $runtime 'shared\Microsoft.NETCore.App\6.0.36\hostpolicy.dll'))) {
        NoLinks $file;if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Required loader/runtime file is missing: $file"}
    }
    CheckHash (Join-Path $game 'GameAssembly.dll') ([string](Value $marker 'GameAssemblyHash'))
    CheckHash (Join-Path $game 'UnityPlayer.dll') ([string](Value $marker 'UnityPlayerHash'))
    CheckHash (Join-Path $game 'Ultimate Admiral Dreadnoughts_Data\il2cpp_data\Metadata\global-metadata.dat') ([string](Value $marker 'MetadataHash'))
    $mods=Join-Path $game 'Mods'
    $installed=@(Get-ChildItem -LiteralPath $mods -Force)
    if($installed.Count -ne 2 -or @($installed|Where-Object{$_.PSIsContainer -or $_.Name -notin @('FleetOperations.dll','FleetOpsLabIsolation.dll')}).Count -gt 0){throw 'Independent Mods must contain exactly FleetOperations.dll and FleetOpsLabIsolation.dll.'}
    $payload=Value $marker 'PayloadHashes';$receiptPayload=Value $manifest 'PayloadHashes'
    foreach($name in @('FleetOperations.dll','FleetOpsLabIsolation.dll')) {
        $relative='payload/Mods/'+$name;$expected=[string](Value $payload $relative)
        if([string](Value $receiptPayload $relative) -ine $expected){throw "Payload hash changed since installation: $name"}
        CheckHash (Join-Path $package $relative) $expected
        CheckHash (Join-Path $mods $name) $expected
    }
    foreach($folder in @('Plugins','UserLibs')) {
        $path=Join-Path $game $folder
        if(!(Test-Path -LiteralPath $path -PathType Container) -or @(Get-ChildItem -LiteralPath $path -File -Recurse -Force).Count -gt 0){throw "Unexpected extensions in independent $folder directory."}
    }
    if($ValidateOnly -or $PlanOnly) {
        [pscustomobject]@{Status='VALIDATED_NOT_STARTED';Game=$exe;IndependentUserdata=$data;Runtime=$runtime;GameStarted=$false;RuntimeAcceptance='NOT_RUN'}
        exit 0
    }
    foreach($process in @(Get-Process -Name 'Ultimate Admiral Dreadnoughts' -ErrorAction SilentlyContinue)) {
        $runningPath=$null;try{$runningPath=$process.Path}catch{}
        if($runningPath -and [IO.Path]::GetFullPath($runningPath).Equals($exe,[StringComparison]::OrdinalIgnoreCase)){throw 'This independent game is already running. Close it normally before starting another instance.'}
    }
    $cliHome=Join-Path $package 'runtime\dotnet-home';NoLinks $cliHome
    [IO.Directory]::CreateDirectory($cliHome)|Out-Null
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $playerLog=Join-Path $logs ('Player-'+$stamp+'.log')
    $launchReceipt=Join-Path $logs ('launch-'+$stamp+'.json')
    NoLinks $playerLog;NoLinks $launchReceipt
    $startInfo=New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName=$exe;$startInfo.WorkingDirectory=$game
    $startInfo.UseShellExecute=$false;$startInfo.CreateNoWindow=$true
    # The user explicitly requests an interactive game window.
    $startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Normal
    $startInfo.EnvironmentVariables['DOTNET_ROOT']=$runtime
    $startInfo.EnvironmentVariables['DOTNET_ROOT_X64']=$runtime
    $startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
    $startInfo.EnvironmentVariables['DOTNET_CLI_HOME']=$cliHome
    $startInfo.Arguments='-screen-fullscreen 0 -screen-width 1600 -screen-height 900 -logFile "'+$playerLog+'"'
    $launched=[Diagnostics.Process]::Start($startInfo)
    $receipt=[ordered]@{Status='STARTED_USER_REVIEW_PENDING';StartedUtc=[DateTime]::UtcNow.ToString('o');ProcessId=$launched.Id;Game=$exe;IndependentUserdata=$data;PlayerLog=$playerLog;ExportVersion='0.0.8.1-export-r1';RuntimeAcceptance='USER_REVIEW_PENDING'}
    [IO.File]::WriteAllText($launchReceipt,($receipt|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
    [pscustomobject]$receipt
} catch { [Console]::Error.WriteLine($_.Exception.Message);exit 1 }
