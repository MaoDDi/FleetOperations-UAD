#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourceDirectory = '',
    [string]$TestDirectory = '',
    [string]$OutputDirectory = '',
    [string]$DotnetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = Join-Path $scriptDirectory 'src' }
if ([string]::IsNullOrWhiteSpace($TestDirectory)) { $TestDirectory = Join-Path $scriptDirectory 'tests' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $scriptDirectory 'test-out' }
. (Join-Path $scriptDirectory 'Build-Source.ps1') -SourceDirectory $SourceDirectory -OutputDirectory $OutputDirectory -DotnetPath $DotnetPath -Configuration $Configuration

try {
    $sourceRoot = Resolve-FleetExportDirectory $SourceDirectory 'SourceDirectory'
    $testRoot = Resolve-FleetExportDirectory $TestDirectory 'TestDirectory'
    $context = New-FleetExportContext $OutputDirectory $DotnetPath @($sourceRoot, $testRoot)
    # Every project is generated locally; none of the original evidence-dependent csproj files is loaded.
    $suites = @(
        @{ Name = 'FleetOps.Tests'; Files = @('FleetOpsCore.cs', 'FleetOps.Tests.cs') },
        @{ Name = 'FleetTorpedoOrders.Tests'; Files = @('FleetTorpedoOrders.cs', 'FleetOpsCore.cs', 'FleetTorpedoOrders.Tests.cs') },
        @{ Name = 'FleetGunVolley.Tests'; Files = @('FleetGunVolley.cs', 'FleetGunVolley.Tests.cs') },
        @{ Name = 'FleetBarrelLaunchGeometryTests'; Files = @('FleetBarrelLaunchGeometry.cs', 'FleetBarrelLaunchGeometry.Tests.cs') },
        @{ Name = 'FleetNativeSideGeometryTests'; Files = @('FleetNativeSideGeometry.cs', 'FleetGunImpactGeometry.cs', 'FleetNativeSideGeometry.Tests.cs') },
        @{ Name = 'FleetGunDispersionGeometryTests'; Files = @('FleetGunDispersionGeometry.cs', 'FleetGunDispersionGeometry.Tests.cs') },
        @{ Name = 'FleetDispersionOverlayGeometryTests'; Files = @('FleetDispersionOverlayGeometry.cs', 'FleetDispersionOverlayGeometry.Tests.cs') },
        @{ Name = 'GunImpact.GeometryTests'; Files = @('FleetGunImpactGeometry.cs'); External = 'GunImpact.GeometryTests.cs' },
        @{ Name = 'PortablePaths.Tests'; Files = @('FleetOpsPackagePaths.cs'); External = 'PortablePaths.Tests.cs'; Portable = $true }
    )
    $projects = @($suites | ForEach-Object {
        $suite = $_
        $files = @($suite.Files | ForEach-Object { Join-Path $sourceRoot $_ })
        if ($suite.ContainsKey('External')) { $files += Join-Path $testRoot $suite.External }
        [pscustomobject]@{ Name = $suite.Name; Portable = $suite.ContainsKey('Portable');
            Project = (New-FleetExportProject $context $suite.Name $files -Executable) }
    })
    Invoke-FleetExportContext $context {
        Assert-FleetExportSdk $context
        $results = @()
        foreach ($suite in $projects) {
            $dll = Build-FleetExportProject $context $suite.Project $suite.Name $Configuration
            $log = Join-Path $context.Output ('logs/' + $suite.Name + '.test.log')
            Write-FleetExportText $log ''
            Invoke-FleetExportDotnet $context @($dll) $log
            $text = [IO.File]::ReadAllText($log)
            $passed = 0; $failed = 0
            if ($text -match 'TOTAL\s+\d+;\s+PASS(?:ED)?\s+(\d+);\s+FAIL(?:ED)?\s+(\d+)') {
                $passed = [int]$Matches[1]; $failed = [int]$Matches[2]
            }
            elseif ($text -match 'RESULT\s+(\d+)\s+PASS,\s+(\d+)\s+FAIL') {
                $passed = [int]$Matches[1]; $failed = [int]$Matches[2]
            }
            elseif ($text -match '(\d+)\s+passed,\s+(\d+)\s+failed') {
                $passed = [int]$Matches[1]; $failed = [int]$Matches[2]
            }
            else { throw "Test summary not found: $log" }
            if ($passed -le 0 -or $failed -ne 0) { throw "Test suite failed: $($suite.Name)" }
            $results += [pscustomobject]@{ suite = $suite.Name; portable = $suite.Portable; passed = $passed; failed = $failed; log = $log }
        }
        $total = ($results | Measure-Object -Property passed -Sum).Sum
        $baseTotal = ($results | Where-Object { !$_.portable } | Measure-Object -Property passed -Sum).Sum
        $portableTotal = ($results | Where-Object portable | Measure-Object -Property passed -Sum).Sum
        $receipt = [pscustomobject]@{ utc = [DateTime]::UtcNow.ToString('o'); suites = $results; passed = $total; failed = 0;
            baseEightPassed = $baseTotal; portablePathsPassed = $portableTotal;
            boundary = 'Pure console tests only. No game, Unity/native execution or visual acceptance.' }
        Write-FleetExportText (Join-Path $context.Output 'test-result.json') ($receipt | ConvertTo-Json -Depth 5)
        Write-Host "TEST PASS: $baseTotal base tests (eight suites) + $portableTotal portable-path tests = $total. No game was started."
    }
}
catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
