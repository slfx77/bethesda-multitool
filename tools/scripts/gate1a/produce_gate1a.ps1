#Requires -Version 7
<#
.SYNOPSIS
    Produces the gate-1a artifacts (GLB, Blender package, exact dump, glb fidelity report) for every cover and
    floor file of the cut-1a cover manifest, one BMT process at a time at BelowNormal priority.

.DESCRIPTION
    1. gate1a_resolve.py resolves each manifest file to the container the exe will read, SHA-256 checked, in the
       order Cut1aFixtureResolver.cs uses (source, alsoIn, the Steam Final build's Data folder, the installed
       Steam Data folder).
    2. For each resolved file the exe runs, in this order and one at a time:
           mesh convert  <input> <out>/model.glb    --format glb --overwrite [--entry] [--game] [--platform]
           mesh package  <input> <out>/package.zip  --overwrite            [--entry] [--game] [--platform]
           mesh dump     <input> --native --output <out>/dump.json --overwrite [--entry] [--game] [--platform]
           mesh fidelity <input> --format glb --json  (stdout -> <out>/fidelity.json) [--entry] [--game] [--platform]
       --game is fnv or fo3 from the manifest's game; --platform is x360 or ps3 for a big-endian key, read off the
       primary source as Cut1aCoverFile.ConsolePlatform does. Nothing overrides the exe's own memory gate: no
       memory or gate option is ever passed, and a gate refusal is recorded as that command's exit code.
    3. Every exit code, log and produced path is written to <OutRoot>/artifacts.json (schema gate1a-artifacts/1),
       the manifest run_gate1a.py consumes. A failed command leaves that artifact null; the run continues.

.PARAMETER Exe
    The BethesdaMultitool.exe to run (built by the owner; this script never builds).
.PARAMETER Manifest
    The cut-1a cover manifest (default: the repository's checked-in one, located relative to this script).
.PARAMETER SampleRoot
    The Sample directory the manifest's Sample/... sources resolve under (default: BETHESDA_TEST_DATA_ROOT or
    C:\dev\Multitool\BethesdaMultitool\Sample).
.PARAMETER OutRoot
    Where artifacts go (default: <repo>/TestOutput/gate1a-<yyyyMMdd-HHmm>).
.PARAMETER Roles
    Manifest roles to produce (default cover,floor; declined controls are not producible models).
.PARAMETER Limit
    Produce only the first N resolved files (0 = all).
.PARAMETER Filter
    Only files whose entry or key contains this substring (case-insensitive).
.PARAMETER TimeoutSeconds
    Per-command timeout; a timed-out process is killed and recorded as exit code -1 with "timeout".
.PARAMETER RunOracles
    After production, run run_gate1a.py over artifacts.json into <OutRoot>/receipt.
.PARAMETER Python
    The Python 3 interpreter (default: python).

.EXAMPLE
    pwsh -NoProfile -File tools/scripts/gate1a/produce_gate1a.ps1 -Exe src\BethesdaMultitool\bin\Release\net10.0\BethesdaMultitool.exe -RunOracles
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Manifest,
    [string]$SampleRoot,
    [string]$OutRoot,
    [string[]]$Roles = @('cover', 'floor'),
    [int]$Limit = 0,
    [string]$Filter,
    [int]$TimeoutSeconds = 1800,
    [switch]$RunOracles,
    [string]$Python = 'python'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..\..')).Path
if (-not $Manifest) { $Manifest = Join-Path $repoRoot 'tests\BethesdaMultitool.Tests\Core\Modeling\Samples\cut1a-cover-manifest.json' }
if (-not $SampleRoot) {
    $SampleRoot = if ($env:BETHESDA_TEST_DATA_ROOT) { $env:BETHESDA_TEST_DATA_ROOT } else { 'C:\dev\Multitool\BethesdaMultitool\Sample' }
}
if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot ('TestOutput\gate1a-' + (Get-Date -Format 'yyyyMMdd-HHmm')) }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "The exe was not found: $Exe" }
if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) { throw "The manifest was not found: $Manifest" }
if (-not (Test-Path -LiteralPath $SampleRoot -PathType Container)) { throw "The sample root was not found: $SampleRoot" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$OutRoot = (Resolve-Path -LiteralPath $OutRoot).Path

function Invoke-Logged {
    <# Runs one process at BelowNormal priority with stdout/stderr captured to files; returns exit code, or -1 on timeout.
       When -Timing (a hashtable) is passed it receives elapsedSeconds and peakWorkingSetBytes: the OS peak working set of
       the process, sampled once per second while it runs (a process that has exited no longer reports it), so the value
       is the peak up to the last sample before exit; peakSampleCount says how many samples were taken. #>
    param([string]$FilePath, [string[]]$Arguments, [string]$StdOut, [string]$StdErr, [int]$Timeout, [hashtable]$Timing)
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    $outWriter = [System.IO.StreamWriter]::new($StdOut, $false, [System.Text.UTF8Encoding]::new($false))
    $errWriter = [System.IO.StreamWriter]::new($StdErr, $false, [System.Text.UTF8Encoding]::new($false))
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $peak = 0L
    $samples = 0
    try {
        $null = $process.Start()
        try { $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { Write-Warning "priority not set: $_" }
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($outWriter.BaseStream)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($errWriter.BaseStream)
        $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
        $exited = $false
        while (-not $exited) {
            $exited = $process.WaitForExit(1000)
            try { $process.Refresh(); $value = $process.PeakWorkingSet64; if ($value -gt $peak) { $peak = $value }; $samples++ } catch { }
            if (-not $exited -and [DateTime]::UtcNow -gt $deadline) { break }
        }
        if (-not $exited) {
            try { $process.Kill($true) } catch { }
            $process.WaitForExit()
            [void]$outTask.Wait(5000); [void]$errTask.Wait(5000)
            return -1
        }
        $outTask.Wait(); $errTask.Wait()
        $process.WaitForExit()
        return $process.ExitCode
    } finally {
        $stopwatch.Stop()
        if ($null -ne $Timing) {
            $Timing['elapsedSeconds'] = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            $Timing['peakWorkingSetBytes'] = $peak
            $Timing['peakSampleCount'] = $samples
        }
        $outWriter.Dispose(); $errWriter.Dispose(); $process.Dispose()
    }
}

# Step 1: resolution (Python, SHA-256 checked).
$resolvedPath = Join-Path $OutRoot 'resolved.json'
$resolveArgs = @((Join-Path $scriptDir 'gate1a_resolve.py'), '--manifest', $Manifest, '--sample-root', $SampleRoot, '--out', $resolvedPath, '--roles', ($Roles -join ','))
if ($Limit -gt 0) { $resolveArgs += @('--limit', "$Limit") }
if ($Filter) { $resolveArgs += @('--filter', $Filter) }
$resolveExit = Invoke-Logged -FilePath $Python -Arguments $resolveArgs -StdOut (Join-Path $OutRoot 'resolve.log') -StdErr (Join-Path $OutRoot 'resolve.err') -Timeout 3600
if (-not (Test-Path -LiteralPath $resolvedPath)) { throw "resolution produced no output (exit $resolveExit); see $OutRoot\resolve.err" }
$resolved = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json -AsHashtable
Write-Host ("resolved {0}, unresolved {1}" -f $resolved.counts.resolved, $resolved.counts.unresolved)

# Step 2: production, one process at a time.
$samples = [System.Collections.Generic.List[hashtable]]::new()
$index = 0
foreach ($sample in $resolved.samples) {
    $index++
    $safe = ($sample.entry -replace '[^A-Za-z0-9._-]', '_')
    $keySafe = ($sample.key -replace '[^A-Za-z0-9._-]', '_')
    $dir = Join-Path $OutRoot ('samples\{0:d4}_{1}_{2}' -f $index, $keySafe, $safe)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $record = @{
        id = $sample.id; key = $sample.key; role = $sample.role; entry = $sample.entry; sha256 = $sample.sha256
        source = $sample.resolution.container; sourceStep = $sample.resolution.step; sourceLabel = $sample.resolution.label
        platform = $sample.platform; game = $sample.game; directory = $dir
        glb = $null; package = $null; dump = $null; fidelity = $null
        exitCodes = @{}; logs = @{}; timing = @{}
    }
    if ($sample.resolution.status -ne 'resolved') {
        $record.exitCodes = @{ resolve = 'unresolved' }
        $record.tried = $sample.resolution.tried
        $samples.Add($record)
        Write-Host ("[{0}/{1}] {2}: unresolved" -f $index, $resolved.samples.Count, $sample.entry)
        continue
    }
    $input = $sample.resolution.container
    $common = @()
    if ($sample.resolution.containerKind -eq 'bsa') { $common += @('--entry', $sample.resolution.exeEntry) }
    if ($sample.game) { $common += @('--game', $sample.game) }
    if ($sample.platform) { $common += @('--platform', $sample.platform) }
    $commands = @(
        @{ name = 'convert';  args = @('mesh', 'convert', $input, (Join-Path $dir 'model.glb'), '--format', 'glb', '--overwrite') + $common; artifact = 'glb';      path = (Join-Path $dir 'model.glb') },
        @{ name = 'package';  args = @('mesh', 'package', $input, (Join-Path $dir 'package.zip'), '--overwrite') + $common;                artifact = 'package';  path = (Join-Path $dir 'package.zip') },
        @{ name = 'dump';     args = @('mesh', 'dump', $input, '--native', '--output', (Join-Path $dir 'dump.json'), '--overwrite') + $common; artifact = 'dump';  path = (Join-Path $dir 'dump.json') },
        @{ name = 'fidelity'; args = @('mesh', 'fidelity', $input, '--format', 'glb', '--json') + $common;                                 artifact = 'fidelity'; path = (Join-Path $dir 'fidelity.json'); stdoutIsArtifact = $true }
    )
    Write-Host ("[{0}/{1}] {2} ({3}, {4}{5})" -f $index, $resolved.samples.Count, $sample.entry, $sample.resolution.step, $sample.game, $(if ($sample.platform) { ', ' + $sample.platform } else { '' }))
    foreach ($command in $commands) {
        $stdout = if ($command.ContainsKey('stdoutIsArtifact')) { $command.path } else { Join-Path $dir ($command.name + '.log') }
        $stderr = Join-Path $dir ($command.name + '.err')
        $record.logs[$command.name] = @{ stdout = $stdout; stderr = $stderr; arguments = $command.args }
        $timing = @{}
        try {
            $exit = Invoke-Logged -FilePath $Exe -Arguments $command.args -StdOut $stdout -StdErr $stderr -Timeout $TimeoutSeconds -Timing $timing
        } catch {
            $exit = -2
            Add-Content -LiteralPath $stderr -Value ("launcher failure: " + $_.Exception.Message)
        }
        $record.exitCodes[$command.name] = $exit
        $record.timing[$command.name] = $timing
        # mesh convert treats its output argument as a directory when the input is an archive entry and writes
        # <output>/<archive name>/<entry path>.glb under it; a loose file lands at the path itself. Record the GLB found.
        if ($command.name -eq 'convert' -and (Test-Path -LiteralPath $command.path -PathType Container)) {
            $found = @(Get-ChildItem -LiteralPath $command.path -Recurse -File -Filter '*.glb' | Select-Object -First 1)
            if ($found.Count -eq 1) { $command.path = $found[0].FullName }
        }
        $produced = (Test-Path -LiteralPath $command.path -PathType Leaf) -and ((Get-Item -LiteralPath $command.path).Length -gt 0)
        if ($exit -eq 0 -and $produced) { $record[$command.artifact] = $command.path }
        $peakMiB = if ($timing.ContainsKey('peakWorkingSetBytes')) { [Math]::Round($timing.peakWorkingSetBytes / 1MB) } else { '?' }
        Write-Host ("    {0}: exit {1}{2} ({3} s, peak {4} MiB)" -f $command.name, $exit, $(if ($exit -eq -1) { ' (timeout)' } elseif (-not $produced -and $exit -eq 0) { ' (no output file)' } else { '' }), $timing.elapsedSeconds, $peakMiB)
    }
    # A producing command that exits nonzero because the writer refused the item (Unsupported:), skipped it
    # (Skipped:, exit 0 with no file: no drawable geometry) or the reader declined the input is a decision, not a
    # failure: the sample is declared partial so run_gate1a.py reports its missing hop as not applicable. Any other
    # missing artifact stays a producing failure.
    $reasons = @()
    foreach ($command in $commands) {
        if ($record.exitCodes[$command.name] -eq 0 -and $null -ne $record[$command.artifact]) { continue }
        $text = if (Test-Path -LiteralPath $record.logs[$command.name].stdout) { Get-Content -LiteralPath $record.logs[$command.name].stdout -Raw } else { '' }
        $text += if (Test-Path -LiteralPath $record.logs[$command.name].stderr) { Get-Content -LiteralPath $record.logs[$command.name].stderr -Raw } else { '' }
        $line = [regex]::Match($text, '(?m)^(Unsupported|Skipped|Declined|Not converted|reason|status):.*$|"failure":\s*"[^"]+"')
        if ($line.Success) { $reasons += ($command.name + ': ' + $line.Value.Trim()) }
        elseif ($text -match '(?i)declin|later-cut') { $reasons += ($command.name + ': declined') }
    }
    if ($reasons.Count -gt 0) { $record.partial = $true; $record.partialReasons = $reasons }
    $samples.Add($record)
}

$manifestDocument = @{
    schema = 'gate1a-artifacts/1'
    produced = (Get-Date).ToUniversalTime().ToString('o')
    exe = @{ path = $Exe; sha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $Exe).Length }
    coverManifest = @{ path = $Manifest; sha256 = (Get-FileHash -LiteralPath $Manifest -Algorithm SHA256).Hash.ToLowerInvariant() }
    sampleRoot = $SampleRoot
    resolved = $resolvedPath
    samples = $samples
}
$artifactsPath = Join-Path $OutRoot 'artifacts.json'
$manifestDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactsPath -Encoding utf8NoBOM
$complete = @($samples | Where-Object { $_.glb -and $_.package -and $_.dump -and $_.fidelity }).Count
Write-Host ("artifacts manifest: {0} ({1} of {2} samples complete)" -f $artifactsPath, $complete, $samples.Count)

if ($RunOracles) {
    $receiptDir = Join-Path $OutRoot 'receipt'
    $oracleExit = Invoke-Logged -FilePath $Python -Arguments @((Join-Path $scriptDir 'run_gate1a.py'), $artifactsPath, '--out', $receiptDir) -StdOut (Join-Path $OutRoot 'oracles.log') -StdErr (Join-Path $OutRoot 'oracles.err') -Timeout 86400
    Write-Host ("oracles: exit {0}; receipt {1}" -f $oracleExit, (Join-Path $receiptDir 'receipt.md'))
    exit $oracleExit
}
exit 0
