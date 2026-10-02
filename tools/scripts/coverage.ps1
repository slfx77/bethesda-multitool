<#
.SYNOPSIS
    Opt-in code coverage for the test suite via coverlet static IL instrumentation.

.DESCRIPTION
    STATUS (2026-07-20): WORKING. Full default suite = ~2 min wall (vs ~16 s uninstrumented),
    64% line coverage on BethesdaMultitool.dll at first measurement.

    Coverlet (static IL rewrite, no profiler) is the ONLY working coverage engine for this
    repo. The Microsoft dynamic-instrumentation engine — Visual Studio, dotnet-coverage,
    the MTP CodeCoverage extension, VSTest --collect:"XPlat Code Coverage" — deadlocks
    instrumenting BethesdaMultitool.dll (collector burns 200-300 s CPU, balloons to ~8 GB,
    test host frozen; reproduced 2026-07-19 on a single test class). Do not re-try it.

    Scope is locked to [BethesdaMultitool]* with the test assembly and EsmAnalyzer excluded.
    Coverage stays OUT of the default test path — run this script only when you want numbers.

.PARAMETER FilterClass
    Optional fully-qualified test class name to restrict the run (MTP --filter-class).

.PARAMETER SkipBuild
    Skip the Fast-profile test build (use the existing Release binaries for that profile).

.PARAMETER CoverletExe
    Path to coverlet.exe. Default: resolve "coverlet" from PATH, else the repo-local tool
    (dotnet tool run coverlet). If `dotnet tool install coverlet.console` fails with
    "Unable to load the service index" (this machine's dotnet NuGet stack is flaky while
    curl works), download the nupkg manually and install with --tool-path:
      curl -sL -o coverlet.nupkg https://api.nuget.org/v3-flatcontainer/coverlet.console/6.0.4/coverlet.console.6.0.4.nupkg
      dotnet tool install coverlet.console --tool-path <dir> --add-source <dir-with-nupkg> --version 6.0.4
#>
[CmdletBinding()]
param(
    [string]$FilterClass,
    [switch]$SkipBuild,
    [string]$CoverletExe
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$buildModule = Join-Path $repoRoot 'shared\Multitool.Shared\tools\scripts\Multitool.Build.psm1'
Import-Module $buildModule -Force
$testProject = Join-Path $repoRoot 'tests\BethesdaMultitool.Tests\BethesdaMultitool.Tests.csproj'
$outDir = Join-Path $repoRoot 'TestResults\coverage'

if (-not $SkipBuild) {
    Write-Host 'Building test project (BuildTestsOnly, AnalyzerProfile=Fast)...'
    $buildExitCode = Invoke-MultitoolBuild -RepoRoot $repoRoot -Profile Bethesda `
        -Project 'tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj' -Configuration Release `
        -AnalyzerProfile Fast
    if ($buildExitCode -ne 0) { throw 'Test build failed.' }
}

# Query the same build profile so output isolation cannot leave coverage using stale Full binaries.
$testDll = & dotnet msbuild $testProject -nologo -verbosity:quiet -getProperty:TargetPath `
    -p:AnalyzerProfile=Fast -p:BuildTestsOnly=true -p:Configuration=Release -p:TargetFramework=net10.0
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Fast-profile test assembly.' }
$testDll = "$testDll".Trim()
$testExe = [IO.Path]::ChangeExtension($testDll, '.exe')
if (-not (Test-Path -LiteralPath $testDll -PathType Leaf) -or -not (Test-Path -LiteralPath $testExe -PathType Leaf)) {
    throw 'Fast-profile test binaries are missing. Run coverage.ps1 without -SkipBuild first.'
}

if (-not $CoverletExe) {
    $cmd = Get-Command coverlet -ErrorAction SilentlyContinue
    if ($cmd) { $CoverletExe = $cmd.Source }
}

New-Item -ItemType Directory -Force $outDir | Out-Null
$output = Join-Path $outDir 'coverage.cobertura.xml'
$targetArgs = if ($FilterClass) { "--filter-class $FilterClass" } else { '' }

$coverletArgs = @(
    $testDll,
    '--target', $testExe,
    '--include', '[BethesdaMultitool]*',
    '--exclude', '[BethesdaMultitool.Tests]*',
    '--exclude', '[EsmAnalyzer]*',
    '--format', 'cobertura',
    '--output', $output
)
if ($targetArgs) { $coverletArgs += @('--targetargs', $targetArgs) }

$coverageLease = Enter-MultitoolBuildSlot -RepoRoot $repoRoot
try {
    if ($CoverletExe) {
        & $CoverletExe @coverletArgs
    } else {
        # Falls back to the repo-local tool manifest if one exists.
        dotnet tool run coverlet @coverletArgs
    }
    if ($LASTEXITCODE -ne 0) { throw "coverlet exited with $LASTEXITCODE" }

    Write-Host "Cobertura report: $output"
} finally {
    Exit-MultitoolBuildSlot $coverageLease
}
