<#
.SYNOPSIS Builds and runs the source-contained native runtime bridge fixtures.
.DESCRIPTION Uses synthetic inputs; no game, emulator, SDK download, or captured corpus is required.
The project default is v145. CI selects v143 from its installed Visual Studio 2022 toolchain.
#>
[CmdletBinding()]
param(
    [ValidateSet('v143', 'v145')][string]$PlatformToolset
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVsWhere -PathType Leaf)) {
    throw 'Visual Studio Installer/vswhere.exe is required to locate the C++ build tools.'
}
$taskVsArguments = @('-latest', '-products', '*', '-requires',
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath')
if ($PlatformToolset -eq 'v143') { $taskVsArguments += @('-version', '[17.0,18.0)') }
$taskVisualStudio = & $taskVsWhere @taskVsArguments
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($taskVisualStudio)) {
    throw "Visual Studio C++ build tools were not found for toolset '$PlatformToolset'."
}
$taskMSBuild = Join-Path $taskVisualStudio 'MSBuild/Current/Bin/MSBuild.exe'
$taskSuites = @(
    @{ Name = 'RuntimeConditionModelTests'; Directory = 'NvseRuntimeBridge'; Platform = 'Win32'; Output = 'bin/Release' },
    @{ Name = 'RuntimeLiveInputTests'; Directory = 'NvseRuntimeBridge'; Platform = 'Win32'; Output = 'bin/Release' },
    @{ Name = 'RuntimeCommandHookTests'; Directory = 'NvseRuntimeBridge'; Platform = 'Win32'; Output = 'bin/Release' },
    @{ Name = 'RuntimeGamepadModelTests'; Directory = 'XeniaRuntimeBridge'; Platform = 'x64'; Output = 'bin/GamepadModelTests/Release' },
    @{ Name = 'RuntimeGamepadControlTests'; Directory = 'XeniaRuntimeBridge'; Platform = 'x64'; Output = 'bin/GamepadControlTests/Release' }
)
foreach ($taskSuite in $taskSuites) {
    $taskProjectRoot = Join-Path $taskRoot ('tools/' + $taskSuite.Directory)
    $taskProject = Join-Path $taskProjectRoot ($taskSuite.Name + '.vcxproj')
    $taskBuildArguments = @($taskProject, '/t:Build', '/p:Configuration=Release',
        ('/p:Platform=' + $taskSuite.Platform), '/m:1', '/nr:false', '/nologo', '/v:minimal')
    if ($PlatformToolset) { $taskBuildArguments += '/p:PlatformToolset=' + $PlatformToolset }
    Write-Host "Building $($taskSuite.Name)"
    & $taskMSBuild @taskBuildArguments
    if ($LASTEXITCODE -ne 0) { throw "$($taskSuite.Name) build failed with exit code $LASTEXITCODE." }
    $taskExecutable = Join-Path $taskProjectRoot ($taskSuite.Output + '/' + $taskSuite.Name + '.exe')
    Write-Host "Running $($taskSuite.Name) synthetic fixtures"
    & $taskExecutable
    if ($LASTEXITCODE -ne 0) { throw "$($taskSuite.Name) tests failed with exit code $LASTEXITCODE." }
}
