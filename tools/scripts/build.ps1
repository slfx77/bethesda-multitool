<#
.SYNOPSIS Builds an application through its pinned shared build coordinator.
.DESCRIPTION Install this entry point as tools/scripts/build.ps1 in a consumer.
Bethesda retains its tests-only and isolated defaults; Full includes its GUI targets.
AnalyzerProfile defaults to Development; pack/publish require Full. SkipAnalyzers selects Fast.
CompilerParallel controls compiler/analyzer concurrency; omitted preserves the compiler default.
Property accepts MSBuild properties without the -p: prefix. DryRun is observational.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('build', 'test', 'pack', 'publish', 'restore')][string]$Action = 'build',
    [string]$Project,
    [string]$Framework,
    [string]$Configuration = 'Release',
    [ValidateSet('Development', 'Full', 'Fast')][string]$AnalyzerProfile,
    [switch]$SkipAnalyzers,
    [Nullable[bool]]$CompilerParallel = $null,
    [int]$MaxNodes = 0,
    [int]$MaxConcurrentBuilds = 2,
    [double]$CompilerHeapCapGB = 0,
    [double]$MinFreeGB = 4,
    [switch]$Full,
    [switch]$Isolated,
    [switch]$Shared,
    [double]$ServerBudgetGB = 2,
    [switch]$ShutdownAfter,
    [int]$TimeoutMinutes = 90,
    [switch]$DryRun,
    [string[]]$Property,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$BuildArgs
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskModule = Join-Path $taskRoot 'shared/Multitool.Shared/tools/scripts/Multitool.Build.psm1'
if (-not (Test-Path -LiteralPath $taskModule)) {
    throw 'The pinned Foundation build coordinator is missing. Authenticate with access to the private Multitool.Shared repository and run git submodule update --init --recursive. See README.md, Build from Source.'
}
Import-Module $taskModule -Force
$taskProfile = if (Test-Path -LiteralPath (Join-Path $taskRoot 'src/BethesdaMultitool/BethesdaMultitool.csproj')) { 'Bethesda' }
    elseif (Test-Path -LiteralPath (Join-Path $taskRoot 'src/CorpusTool/CorpusTool.csproj')) { 'Corpus' }
    else { 'Application' }
exit (Invoke-MultitoolBuild -RepoRoot $taskRoot -Profile $taskProfile @PSBoundParameters)
