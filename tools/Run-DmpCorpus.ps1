<#
.SYNOPSIS
Builds and validates the complete DMP-to-ESM corpus sequentially.

.DESCRIPTION
Converts every selected .dmp under Sample/MemoryDump with planner types set to
all, converter validation enabled, and the July then April dialogue CSVs. Each
successful output is subsequently deep-validated, parsed for dialogue stats,
and analyzed for ESM coverage. Structural dialogue-identity telemetry is
required even when a dump emits no dialogue section. The default Release CLI
and analyzer are rebuilt before conversion; all binaries, inputs, structured
conversion events, and ESM outputs are SHA-256-bound to the manifest. Per-dump
logs and an incrementally updated corpus-results.csv are written in the layout
consumed by Verify-DmpCorpus.ps1.

The runner is intentionally sequential because one conversion can consume
roughly 2 GiB of working set. An individual dump failure is recorded and does
not prevent later corpus rows from running.

.PARAMETER DumpFilter
File-name filter applied directly beneath DumpDirectory. Defaults to *.dmp.
Use an exact name, such as Fallout_Release_Beta.xex10.dmp, for a smoke run.

.PARAMETER MasterEsm
FalloutNV.esm used as the retail master. Defaults to the installed Steam copy.

.PARAMETER OutputDirectory
New artifact directory. It must be empty so stale reports cannot contaminate a
certification run.

.PARAMETER OutputTag
Safe suffix placed between each dump stem and .esm. Defaults to corpus.

.PARAMETER ListOnly
Validate inputs and print the selected dumps without creating artifacts.

.EXAMPLE
pwsh tools/Run-DmpCorpus.ps1 -ListOnly

.EXAMPLE
pwsh tools/Run-DmpCorpus.ps1 `
  -DumpFilter Fallout_Release_Beta.xex10.dmp `
  -OutputDirectory TestOutput/corpus-smoke
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $DumpDirectory,
    [string] $DumpFilter = '*.dmp',
    [string] $MasterEsm = 'E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm',
    [string] $OutputDirectory,
    [string] $JulyDialogueCsv,
    [string] $AprilDialogueCsv,
    [string] $CellAuthority,
    [string] $BethesdaMultitoolDll,
    [string] $EsmAnalyzerDll,
    [string] $DotnetPath = 'dotnet',
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $OutputTag = 'corpus',
    [switch] $ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RequiredDirectory {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description directory was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description file was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-DotnetExecutable {
    param([Parameter(Mandatory)] [string] $Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    $command = Get-Command -Name $Path -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        throw "dotnet executable was not found: $Path"
    }

    return $command.Source
}

function Format-CommandArgument {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '([\\]*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Text
    )

    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }

    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Convert-ProcessBytes {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Bytes)

    if ($Bytes.Length -eq 0) {
        return ''
    }

    # The CLI normally emits UTF-8, but Spectre can inherit the Windows OEM
    # console page before Program switches encodings. Decode strictly first so
    # OEM box-drawing bytes cannot silently turn into replacement characters.
    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    }
    catch [Text.DecoderFallbackException] {
        [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
        $codePage = [Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage
        return [Text.Encoding]::GetEncoding($codePage).GetString($Bytes)
    }
}

function Invoke-DotnetDll {
    param(
        [Parameter(Mandatory)] [string] $Dotnet,
        [Parameter(Mandatory)] [string] $Dll,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $LogPath
    )

    $allArguments = @($Dll) + $Arguments
    $displayCommand = @($Dotnet) + $allArguments |
        ForEach-Object { Format-CommandArgument $_ }
    $header = '# Command: ' + ($displayCommand -join ' ') + [Environment]::NewLine
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()

    try {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $Dotnet
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $allArguments) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Process.Start returned false.'
        }

        $stdoutBuffer = [IO.MemoryStream]::new()
        $stderrBuffer = [IO.MemoryStream]::new()
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutBuffer)
        $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrBuffer)
        $process.WaitForExit()
        $null = $stdoutTask.GetAwaiter().GetResult()
        $null = $stderrTask.GetAwaiter().GetResult()
        $stdout = Convert-ProcessBytes $stdoutBuffer.ToArray()
        $stderr = Convert-ProcessBytes $stderrBuffer.ToArray()
        $stdoutBuffer.Dispose()
        $stderrBuffer.Dispose()
        $exitCode = $process.ExitCode
        $process.Dispose()

        $text = $header + $stdout
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            if (-not $text.EndsWith([Environment]::NewLine, [StringComparison]::Ordinal)) {
                $text += [Environment]::NewLine
            }

            $text += '# Standard error:' + [Environment]::NewLine + $stderr
        }

        Write-Utf8Text $LogPath $text
        return [pscustomobject]@{
            ExitCode = $exitCode
            Text = $text
            Seconds = $stopwatch.Elapsed.TotalSeconds
        }
    }
    catch {
        $text = $header + '# Process launch failed: ' + $_.Exception.Message + [Environment]::NewLine
        Write-Utf8Text $LogPath $text
        return [pscustomobject]@{
            ExitCode = -1
            Text = $text
            Seconds = $stopwatch.Elapsed.TotalSeconds
        }
    }
    finally {
        $stopwatch.Stop()
    }
}

function Invoke-DotnetBuildWithRetry {
    param(
        [Parameter(Mandatory)] [string] $Dotnet,
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $LogPath,
        [int] $Attempts = 3
    )

    $logs = [Collections.Generic.List[string]]::new()
    $last = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $attemptLog = "$LogPath.attempt-$attempt"
        $last = Invoke-DotnetDll $Dotnet 'build' @(
            $Project,
            '-c', 'Release',
            '--no-restore',
            '--disable-build-servers',
            '-nodeReuse:false',
            '-p:BuildTestsOnly=true',
            '-p:SkipAnalyzers=true',
            '-p:UseSharedCompilation=false'
        ) $attemptLog
        $logs.Add("# Build attempt $attempt/$Attempts" + [Environment]::NewLine + $last.Text)
        Remove-Item -LiteralPath $attemptLog -Force -ErrorAction SilentlyContinue
        if ($last.ExitCode -eq 0) {
            break
        }

        if ($attempt -lt $Attempts) {
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    Write-Utf8Text $LogPath ($logs -join ([Environment]::NewLine + [Environment]::NewLine))
    return $last
}

function New-SkippedInvocation {
    param(
        [Parameter(Mandatory)] [string] $LogPath,
        [Parameter(Mandatory)] [string] $Reason
    )

    $text = '# Skipped: ' + $Reason + [Environment]::NewLine
    Write-Utf8Text $LogPath $text
    return [pscustomobject]@{ ExitCode = -1; Text = $text; Seconds = 0.0 }
}

function Ensure-CoverageReports {
    param([Parameter(Mandatory)] [string] $Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        New-Item -ItemType Directory -Path $Directory | Out-Null
    }

    $headers = [ordered]@{
        'record_coverage.csv' =
            'record_type,count,classification,parser_owner,encoder_owner,example_form_ids'
        'subrecord_coverage.csv' =
            'record_type,subrecord,data_length,count,classification,schema_kind,uses_raw_byte_array,is_intentional_raw,coverage_note,parser_owner,encoder_owner,example_form_ids'
        'script_bytecode_coverage.csv' =
            'record_type,form_id,block_index,scda_length,schr_compiled_size,schr_ref_object_count,actual_reference_slots,schr_variable_count,actual_variables,compiled_size_matches,ref_count_matches,variable_count_matches,walked_to_end,multi_byte_read_count,multi_byte_byte_count,has_diagnostics,diagnostics'
    }

    foreach ($entry in $headers.GetEnumerator()) {
        $path = Join-Path $Directory $entry.Key
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Utf8Text $path ($entry.Value + [Environment]::NewLine)
        }
    }
}

function Get-DialogueTableCount {
    param(
        [Parameter(Mandatory)] [string] $Text,
        [Parameter(Mandatory)] [string] $Label
    )

    $pattern = '(?m)^\s*│\s*' + [regex]::Escape($Label) + '\s*│\s*([0-9][0-9,. \t\u00A0]*)\s*│'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        return $null
    }

    return [int64]($match.Groups[1].Value -replace '[^0-9]', '')
}

function Get-Sha256 {
    param([Parameter(Mandatory)] [string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Copy-ToolSnapshot {
    param(
        [Parameter(Mandatory)] [string] $DllPath,
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $Description
    )

    $sourceDirectory = Split-Path -Parent $DllPath
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $sourceDirectory -Force |
        Copy-Item -Destination $Destination -Recurse -Force

    $snapshotDll = Join-Path $Destination (Split-Path -Leaf $DllPath)
    return Resolve-RequiredFile $snapshotDll "$Description snapshot"
}

function Get-DirectoryFileManifest {
    param([Parameter(Mandatory)] [string] $Directory)

    return @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse -Force |
            Sort-Object FullName |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    RelativePath = [IO.Path]::GetRelativePath($Directory, $_.FullName)
                    Bytes = $_.Length
                    Sha256 = Get-Sha256 $_.FullName
                }
            }
    )
}

function Test-JsonObjectProperties {
    param(
        [Parameter(Mandatory)] [object] $Value,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Names
    )

    if ($Value -isnot [pscustomobject]) {
        return $false
    }

    $available = @($Value.PSObject.Properties.Name)
    return @($Names | Where-Object { $available -notcontains $_ }).Count -eq 0
}

function Read-ConversionEventLog {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            IsValid = $false
            Error = 'event log was not created'
            Events = [object[]]@()
        }
    }

    $events = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    try {
        foreach ($line in Get-Content -LiteralPath $Path) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $event = $line | ConvertFrom-Json -ErrorAction Stop
            if (-not (Test-JsonObjectProperties $event @('SchemaVersion', 'Kind'))) {
                throw "line $lineNumber is not a conversion-event object"
            }
            if ([int]$event.SchemaVersion -ne 1) {
                throw "line $lineNumber uses unsupported schema version '$($event.SchemaVersion)'"
            }

            $required = switch ($event.Kind) {
                'PhaseStart' { @('Phase', 'TotalItems') }
                'Event' { @('Timestamp', 'Severity', 'Phase', 'FormType', 'FormId', 'Code', 'Message') }
                'PhaseEnd' {
                    @('Phase', 'RecordsConsidered', 'RecordsEmitted', 'RecordsSkipped', 'RecordsFailed')
                }
                'Complete' {
                    @(
                        'RecordsConsidered', 'RecordsEmitted', 'RecordsSkipped', 'RecordsFailed',
                        'OverridesEmitted', 'NewRecordsEmitted', 'CellsMerged', 'Warnings', 'Errors',
                        'OutputBytes', 'ElapsedMilliseconds', 'EmittedByType', 'SkippedByType',
                        'DropReasonCounts'
                    )
                }
                default { throw "line $lineNumber has unknown event kind '$($event.Kind)'" }
            }
            if (-not (Test-JsonObjectProperties $event $required)) {
                throw "line $lineNumber is missing required '$($event.Kind)' properties"
            }

            $events.Add($event)
        }

        if ($events.Count -eq 0) {
            throw 'event log contains no events'
        }

        return [pscustomobject]@{
            IsValid = $true
            Error = ''
            Events = [object[]]$events.ToArray()
        }
    }
    catch {
        return [pscustomobject]@{
            IsValid = $false
            Error = $_.Exception.Message
            Events = [object[]]@()
        }
    }
}

function Get-SuppressedRecordIdsFromEvents {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Events,
        [Parameter(Mandatory)] [ValidateSet('INFO', 'PACK', 'SCPT')] [string] $RecordType
    )

    $codes = if ($RecordType -eq 'SCPT') {
        @('script.suppress-unsafe-reference-table', 'script.suppress-post-verdict-reference-table')
    }
    else {
        @('quest-variable.record-suppressed')
    }

    return @(
        $Events | Where-Object {
            $_.Kind -eq 'Event' -and
            $_.FormType -eq $RecordType -and
            $codes -contains $_.Code -and
            $_.FormId -match '^0x[0-9A-Fa-f]{8}$'
        } | ForEach-Object { $_.FormId.Substring(2).ToUpperInvariant() } |
            Sort-Object -Unique
    )
}

function Test-SuppressionEvents {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Events)

    $scriptCodes = @(
        'script.suppress-unsafe-reference-table',
        'script.suppress-post-verdict-reference-table'
    )
    foreach ($event in $Events | Where-Object {
        $_.Kind -eq 'Event' -and
        ($_.Code -eq 'quest-variable.record-suppressed' -or $scriptCodes -contains $_.Code)
    }) {
        if ($event.FormId -notmatch '^0x[0-9A-Fa-f]{8}$') {
            return $false
        }
        if ($scriptCodes -contains $event.Code) {
            if ($event.FormType -ne 'SCPT') {
                return $false
            }
        }
        elseif ($event.FormType -notin @('INFO', 'PACK')) {
            return $false
        }
    }

    return $true
}

function Test-DialogueIdentityEvents {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Events)

    $summary = @($Events | Where-Object {
        $_.Kind -eq 'Event' -and $_.Code -eq 'dialogue.identity.summary'
    })
    if ($summary.Count -ne 1) {
        return $false
    }

    $categoryCodes = @(
        'dialogue.identity.master-anchor',
        'dialogue.identity.shared-child-anchor',
        'dialogue.identity.prototype-distinct',
        'dialogue.identity.ambiguous'
    )
    $identities = @($Events | Where-Object {
        $_.Kind -eq 'Event' -and $categoryCodes -contains $_.Code
    })
    $unknownIdentityEvents = @($Events | Where-Object {
        $_.Kind -eq 'Event' -and
        $_.Code -like 'dialogue.identity.*' -and
        $_.Code -ne 'dialogue.identity.summary' -and
        $categoryCodes -notcontains $_.Code
    })
    if ($unknownIdentityEvents.Count -ne 0 -or
        @($identities | Where-Object {
            $_.Severity -ne 'Decision' -or
            $_.FormType -ne 'DIAL' -or
            $_.FormId -notmatch '^0x[0-9A-Fa-f]{8}$'
        }).Count -ne 0) {
        return $false
    }

    return @($identities | Group-Object FormId | Where-Object Count -ne 1).Count -eq 0
}

$buildDefaultBethesdaMultitool = [string]::IsNullOrWhiteSpace($BethesdaMultitoolDll)
$buildDefaultEsmAnalyzer = [string]::IsNullOrWhiteSpace($EsmAnalyzerDll)

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = Resolve-RequiredDirectory $RepositoryRoot 'Repository root'

if ([string]::IsNullOrWhiteSpace($DumpDirectory)) {
    $DumpDirectory = Join-Path $RepositoryRoot 'Sample\MemoryDump'
}
if ([string]::IsNullOrWhiteSpace($JulyDialogueCsv)) {
    $JulyDialogueCsv = Join-Path $RepositoryRoot 'TestOutput\all_dialogue_july.csv'
}
if ([string]::IsNullOrWhiteSpace($AprilDialogueCsv)) {
    $AprilDialogueCsv = Join-Path $RepositoryRoot 'TestOutput\all_dialogue_april.csv'
}
if ([string]::IsNullOrWhiteSpace($CellAuthority)) {
    $CellAuthority = Join-Path $RepositoryRoot 'data\cell_worldspace_authority.json'
}
if ([string]::IsNullOrWhiteSpace($BethesdaMultitoolDll)) {
    $BethesdaMultitoolDll = Join-Path $RepositoryRoot 'src\BethesdaMultitool\bin\Release\net10.0\BethesdaMultitool.dll'
}
if ([string]::IsNullOrWhiteSpace($EsmAnalyzerDll)) {
    $EsmAnalyzerDll = Join-Path $RepositoryRoot 'tools\EsmAnalyzer\bin\Release\net10.0\EsmAnalyzer.dll'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $date = Get-Date -Format 'yyyy-MM-dd'
    $time = Get-Date -Format 'HHmmss'
    $OutputDirectory = Join-Path $RepositoryRoot "TestOutput\corpus-$date\run-$time"
}

if ([string]::IsNullOrWhiteSpace($DumpFilter)) {
    throw 'DumpFilter cannot be empty.'
}

$DumpDirectory = Resolve-RequiredDirectory $DumpDirectory 'DMP corpus'
$MasterEsm = Resolve-RequiredFile $MasterEsm 'Installed FalloutNV master'
$JulyDialogueCsv = Resolve-RequiredFile $JulyDialogueCsv 'July dialogue CSV'
$AprilDialogueCsv = Resolve-RequiredFile $AprilDialogueCsv 'April dialogue CSV'
$CellAuthority = Resolve-RequiredFile $CellAuthority 'Cell authority'
$DotnetPath = Resolve-DotnetExecutable $DotnetPath

$dumps = @(
    Get-ChildItem -LiteralPath $DumpDirectory -File -Filter $DumpFilter |
        Where-Object Extension -IEQ '.dmp' |
        Sort-Object Name
)
if ($dumps.Count -eq 0) {
    throw "No .dmp files matched '$DumpFilter' beneath $DumpDirectory"
}

$duplicateStems = @($dumps | Group-Object BaseName | Where-Object Count -gt 1)
if ($duplicateStems.Count -ne 0) {
    throw 'Selected dumps contain duplicate file stems: ' +
        (($duplicateStems | ForEach-Object Name) -join ', ')
}

$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

Write-Host "DMP corpus: $DumpDirectory"
Write-Host "Selected: $($dumps.Count) dump(s), filter '$DumpFilter'"
Write-Host "Master: $MasterEsm"
Write-Host "Dialogue CSV priority: July, then April"
Write-Host "Output: $outputPath"
Write-Host 'Execution: sequential (one conversion process at a time)'

if ($ListOnly) {
    $BethesdaMultitoolDll = Resolve-RequiredFile $BethesdaMultitoolDll 'BethesdaMultitool CLI DLL'
    $EsmAnalyzerDll = Resolve-RequiredFile $EsmAnalyzerDll 'EsmAnalyzer DLL'
    $dumps | ForEach-Object { Write-Host ('  ' + $_.Name) }
    return
}

if (Test-Path -LiteralPath $outputPath) {
    if (@(Get-ChildItem -LiteralPath $outputPath -Force).Count -ne 0) {
        throw "OutputDirectory must be empty: $outputPath"
    }
}
else {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

if ($buildDefaultBethesdaMultitool) {
    Write-Host 'Building BethesdaMultitool Release CLI...'
    $cliProject = Join-Path $RepositoryRoot 'src\BethesdaMultitool\BethesdaMultitool.csproj'
    $cliBuild = Invoke-DotnetBuildWithRetry $DotnetPath $cliProject (
        Join-Path $outputPath 'build-bethesda-multitool.log')
    if ($cliBuild.ExitCode -ne 0) {
        throw "BethesdaMultitool Release build failed with exit code $($cliBuild.ExitCode)."
    }
}

if ($buildDefaultEsmAnalyzer) {
    Write-Host 'Building EsmAnalyzer Release CLI...'
    $analyzerProject = Join-Path $RepositoryRoot 'tools\EsmAnalyzer\EsmAnalyzer.csproj'
    $analyzerBuild = Invoke-DotnetBuildWithRetry $DotnetPath $analyzerProject (
        Join-Path $outputPath 'build-esm-analyzer.log')
    if ($analyzerBuild.ExitCode -ne 0) {
        throw "EsmAnalyzer Release build failed with exit code $($analyzerBuild.ExitCode)."
    }
}

$BethesdaMultitoolDll = Resolve-RequiredFile $BethesdaMultitoolDll 'BethesdaMultitool CLI DLL'
$EsmAnalyzerDll = Resolve-RequiredFile $EsmAnalyzerDll 'EsmAnalyzer DLL'

$sourceBethesdaMultitoolDll = $BethesdaMultitoolDll
$sourceEsmAnalyzerDll = $EsmAnalyzerDll
$toolchainDirectory = Join-Path $outputPath 'toolchain'
$BethesdaMultitoolDll = Copy-ToolSnapshot $BethesdaMultitoolDll (
    Join-Path $toolchainDirectory 'BethesdaMultitool') 'BethesdaMultitool CLI'
$EsmAnalyzerDll = Copy-ToolSnapshot $EsmAnalyzerDll (
    Join-Path $toolchainDirectory 'EsmAnalyzer') 'EsmAnalyzer'

$cliSha256 = Get-Sha256 $BethesdaMultitoolDll
$analyzerSha256 = Get-Sha256 $EsmAnalyzerDll
$masterSha256 = Get-Sha256 $MasterEsm
$julyCsvSha256 = Get-Sha256 $JulyDialogueCsv
$aprilCsvSha256 = Get-Sha256 $AprilDialogueCsv
$cellAuthoritySha256 = Get-Sha256 $CellAuthority
$bethesdaToolchainFiles = Get-DirectoryFileManifest (Split-Path -Parent $BethesdaMultitoolDll)
$analyzerToolchainFiles = Get-DirectoryFileManifest (Split-Path -Parent $EsmAnalyzerDll)
$gitHead = try {
    (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
}
catch {
    $null
}

Write-Host "Hashing $($dumps.Count) selected DMP input(s)..."
$dumpInputs = @(
    foreach ($dump in $dumps) {
        [pscustomobject][ordered]@{
            Name = $dump.Name
            Path = $dump.FullName
            Bytes = $dump.Length
            Sha256 = Get-Sha256 $dump.FullName
        }
    }
)

$buildInputs = [pscustomobject][ordered]@{
    SchemaVersion = 1
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    GitHead = $gitHead
    RepositoryRoot = $RepositoryRoot
    DumpDirectory = $DumpDirectory
    DumpFilter = $DumpFilter
    OutputTag = $OutputTag
    BethesdaMultitool = [pscustomobject]@{
        Path = $BethesdaMultitoolDll
        SourcePath = $sourceBethesdaMultitoolDll
        Sha256 = $cliSha256
        Files = $bethesdaToolchainFiles
    }
    EsmAnalyzer = [pscustomobject]@{
        Path = $EsmAnalyzerDll
        SourcePath = $sourceEsmAnalyzerDll
        Sha256 = $analyzerSha256
        Files = $analyzerToolchainFiles
    }
    MasterEsm = [pscustomobject]@{ Path = $MasterEsm; Sha256 = $masterSha256 }
    JulyDialogueCsv = [pscustomobject]@{ Path = $JulyDialogueCsv; Sha256 = $julyCsvSha256 }
    AprilDialogueCsv = [pscustomobject]@{ Path = $AprilDialogueCsv; Sha256 = $aprilCsvSha256 }
    CellAuthority = [pscustomobject]@{ Path = $CellAuthority; Sha256 = $cellAuthoritySha256 }
    Dumps = $dumpInputs
}
Write-Utf8Text (Join-Path $outputPath 'corpus-build-inputs.json') (
    $buildInputs | ConvertTo-Json -Depth 6)

$manifestPath = Join-Path $outputPath 'corpus-results.csv'
$results = [Collections.Generic.List[object]]::new()
$ordinal = 0

foreach ($dump in $dumps) {
    $ordinal++
    $rowStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $stem = $dump.BaseName
    $esmPath = Join-Path $outputPath "$stem.$OutputTag.esm"
    $buildLog = Join-Path $outputPath "$stem.build.log"
    $deepLog = Join-Path $outputPath "$stem.deep.log"
    $dialogueLog = Join-Path $outputPath "$stem.dialogue.log"
    $coverageLog = Join-Path $outputPath "$stem.coverage.log"
    $eventLog = Join-Path $outputPath "$stem.events.jsonl"
    $coverageDirectory = Join-Path $outputPath "$stem.coverage"

    Write-Host "[$ordinal/$($dumps.Count)] $($dump.Name)"

    $convertArguments = @(
        'dmp', 'to-esm', $dump.FullName,
        '--pc-esm', $MasterEsm,
        '--output', $esmPath,
        '--planner-types', 'all',
        '--validate',
        '--cell-authority', $CellAuthority,
        '--event-log-jsonl', $eventLog,
        '--dialogue-audio-csv', $JulyDialogueCsv,
        '--dialogue-audio-csv', $AprilDialogueCsv
    )
    $conversion = Invoke-DotnetDll $DotnetPath $BethesdaMultitoolDll $convertArguments $buildLog

    if (Test-Path -LiteralPath $esmPath -PathType Leaf) {
        $deep = Invoke-DotnetDll $DotnetPath $EsmAnalyzerDll @(
            'validate-deep', $esmPath
        ) $deepLog
        $dialogue = Invoke-DotnetDll $DotnetPath $BethesdaMultitoolDll @(
            'dialogue', 'stats', $esmPath
        ) $dialogueLog
        $coverage = Invoke-DotnetDll $DotnetPath $BethesdaMultitoolDll @(
            'esm', 'coverage', $esmPath, '--output', $coverageDirectory
        ) $coverageLog
    }
    else {
        $reason = 'conversion did not produce an ESM artifact'
        $deep = New-SkippedInvocation $deepLog $reason
        $dialogue = New-SkippedInvocation $dialogueLog $reason
        $coverage = New-SkippedInvocation $coverageLog $reason
    }

    Ensure-CoverageReports $coverageDirectory
    $recordCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'record_coverage.csv'))
    $subrecordCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'subrecord_coverage.csv'))
    $scriptCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'script_bytecode_coverage.csv'))
    $eventLogRead = Read-ConversionEventLog $eventLog
    $conversionEvents = @($eventLogRead.Events)
    if (-not $eventLogRead.IsValid) {
        Write-Warning "Invalid conversion event log for $($dump.Name): $($eventLogRead.Error)"
    }

    $buildFlat = $conversion.Text -replace '\s+', ' '
    $conversionPass = $conversion.ExitCode -eq 0 -and $buildFlat -match 'Conversion succeeded\.'
    $reparsePass = $buildFlat -match 'Re-parser read .* record\(s\) without errors\.'
    $semanticPass = $buildFlat -match 'Semantic check passed:'
    $completeEvents = @($conversionEvents | Where-Object Kind -eq 'Complete')
    $eventLogPass =
        $eventLogRead.IsValid -and
        $completeEvents.Count -eq 1 -and
        [int]$completeEvents[0].RecordsFailed -eq 0 -and
        [int]$completeEvents[0].Errors -eq 0
    $failedZero = $eventLogPass
    $suppressionTelemetryPass = Test-SuppressionEvents $conversionEvents
    $dialogueIdentityPass = Test-DialogueIdentityEvents $conversionEvents
    $deepPass = $deep.ExitCode -eq 0 -and $deep.Text -match 'Deep validation OK'

    $infoCount = Get-DialogueTableCount $dialogue.Text 'INFO (Dialogue) records'
    $linkedCount = Get-DialogueTableCount $dialogue.Text 'Linked to topic'
    $unlinkedCount = Get-DialogueTableCount $dialogue.Text 'Unlinked'
    $responseCount = Get-DialogueTableCount $dialogue.Text 'With response text'
    $unlinkedZero =
        $dialogue.ExitCode -eq 0 -and
        $null -ne $infoCount -and
        $null -ne $linkedCount -and
        $null -ne $unlinkedCount -and
        $null -ne $responseCount -and
        $unlinkedCount -eq 0 -and
        ($linkedCount + $unlinkedCount) -eq $infoCount -and
        $responseCount -le $infoCount

    $unparsedRows = @($recordCoverage | Where-Object classification -eq 'Unparsed')
    $unparsedCount = if ($unparsedRows.Count -eq 0) {
        0L
    }
    else {
        [int64](($unparsedRows | Measure-Object -Property count -Sum).Sum)
    }
    $rawGapCount = @(
        $subrecordCoverage | Where-Object {
            $_.uses_raw_byte_array -eq 'True' -and
            $_.is_intentional_raw -ne 'True'
        }
    ).Count
    $coveragePass =
        $coverage.ExitCode -eq 0 -and
        $unparsedCount -eq 0 -and
        $rawGapCount -eq 0

    $hardScriptIssues = @(
        $scriptCoverage | Where-Object {
            $_.compiled_size_matches -ne 'True' -or
            $_.ref_count_matches -ne 'True' -or
            $_.walked_to_end -ne 'True' -or
            $_.has_diagnostics -eq 'True'
        }
    ).Count
    $scdaCountRows = @(
        $subrecordCoverage | Where-Object {
            $_.subrecord -eq 'SCDA'
        }
    )
    $scdaCount = if ($scdaCountRows.Count -eq 0) {
        0L
    }
    else {
        [int64](($scdaCountRows | Measure-Object -Property count -Sum).Sum)
    }
    $scriptCoverageComplete = $scriptCoverage.Count -eq $scdaCount
    $variableTelemetry = @(
        $scriptCoverage | Where-Object variable_count_matches -ne 'True'
    ).Count
    $typeWarningLines = @(
        $conversion.Text -split '\r?\n' |
            Where-Object { $_ -match '(?i)\b(variable type|type mismatch)\b' }
    ).Count

    $success =
        $conversionPass -and
        $failedZero -and
        $reparsePass -and
        $semanticPass -and
        $eventLogPass -and
        $suppressionTelemetryPass -and
        $dialogueIdentityPass -and
        $deepPass -and
        $unlinkedZero -and
        $coveragePass -and
        $scriptCoverageComplete -and
        $hardScriptIssues -eq 0 -and
        $typeWarningLines -eq 0

    $rowStopwatch.Stop()
    $responseLine = [regex]::Match(
        $dialogue.Text,
        '(?m)^.*With response text.*$'
    ).Value.Trim()

    $suppressedInfoIds = @(Get-SuppressedRecordIdsFromEvents $conversionEvents 'INFO')
    $suppressedPackageIds = @(Get-SuppressedRecordIdsFromEvents $conversionEvents 'PACK')
    $suppressedScriptIds = @(Get-SuppressedRecordIdsFromEvents $conversionEvents 'SCPT')
    $outputSha256 = if (Test-Path -LiteralPath $esmPath -PathType Leaf) {
        Get-Sha256 $esmPath
    }
    else {
        ''
    }
    $dumpInput = $dumpInputs[$ordinal - 1]

    $row = [pscustomobject][ordered]@{
        Dump = $dump.Name
        InputBytes = $dump.Length
        InputSha256 = $dumpInput.Sha256
        Success = $success
        Seconds = [Math]::Round($rowStopwatch.Elapsed.TotalSeconds, 1)
        ConvertExit = $conversion.ExitCode
        ConversionPass = $conversionPass
        FailedZero = $failedZero
        ReparsePass = $reparsePass
        SemanticPass = $semanticPass
        EventLogPass = $eventLogPass
        SuppressionTelemetryPass = $suppressionTelemetryPass
        DialogueIdentityPass = $dialogueIdentityPass
        DeepExit = $deep.ExitCode
        DeepPass = $deepPass
        DialogueExit = $dialogue.ExitCode
        UnlinkedZero = $unlinkedZero
        ResponseText = $responseLine
        CoverageExit = $coverage.ExitCode
        CoveragePass = $coveragePass
        ScdaBlocks = $scdaCount
        ScriptCoverageRows = $scriptCoverage.Count
        ScriptCoverageComplete = $scriptCoverageComplete
        ScriptStructuralIssues = $hardScriptIssues
        VariableCountTelemetry = $variableTelemetry
        InfoSuppressions = $suppressedInfoIds.Count
        PackSuppressions = $suppressedPackageIds.Count
        ScptSuppressions = $suppressedScriptIds.Count
        SuppressedInfoFormIds = $suppressedInfoIds -join ';'
        SuppressedPackageFormIds = $suppressedPackageIds -join ';'
        SuppressedScriptFormIds = $suppressedScriptIds -join ';'
        TypeWarningLines = $typeWarningLines
        BethesdaMultitoolSha256 = $cliSha256
        EsmAnalyzerSha256 = $analyzerSha256
        MasterSha256 = $masterSha256
        JulyDialogueCsvSha256 = $julyCsvSha256
        AprilDialogueCsvSha256 = $aprilCsvSha256
        CellAuthoritySha256 = $cellAuthoritySha256
        OutputBytes = if (Test-Path -LiteralPath $esmPath -PathType Leaf) {
            (Get-Item -LiteralPath $esmPath).Length
        } else { 0L }
        OutputSha256 = $outputSha256
        Output = $esmPath
    }
    $results.Add($row)
    $results | Export-Csv -LiteralPath $manifestPath -NoTypeInformation

    Write-Host (
        "  exits convert/deep/dialogue/coverage={0}/{1}/{2}/{3}; success={4}; {5:N1}s" -f
        $conversion.ExitCode,
        $deep.ExitCode,
        $dialogue.ExitCode,
        $coverage.ExitCode,
        $success,
        $rowStopwatch.Elapsed.TotalSeconds
    )
}

$passed = @($results | Where-Object Success).Count
$failed = $results.Count - $passed
Write-Host "Corpus run complete: $passed/$($results.Count) passed; $failed failed."
Write-Host "Manifest: $manifestPath"
Write-Host (
    "Verify: pwsh tools/Verify-DmpCorpus.ps1 -ArtifactDirectory '$outputPath' " +
    "-DumpDirectory '$DumpDirectory' -DumpFilter '$DumpFilter'"
)

if ($failed -ne 0) {
    exit 1
}

exit 0
