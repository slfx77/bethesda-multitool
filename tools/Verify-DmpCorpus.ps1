<#
.SYNOPSIS
Certifies an existing full-DMP conversion artifact directory.

.DESCRIPTION
Evaluates conversion, reparse, semantic, deep-validation, dialogue-linkage,
structural dialogue-identity telemetry, record/subrecord coverage, and
compiled-script integrity for every row in corpus-results.csv. It rejects a
partial or duplicate dump set and verifies SHA-256 bindings for the converter,
analyzer, master, CSVs, cell authority, every DMP, and every output ESM.
Structured JSONL conversion events supply identity and suppression telemetry;
console wording is not their authority. Spectre wrapping is normalized only
for legacy validation summaries.

Standalone SCPT VariableCount telemetry is reported but is not treated as a
hard failure; compiled-size, reference-count, bytecode-walk, and decoder
diagnostics remain hard failures. A zero-INFO output is valid when both linked
and unlinked counts are zero.

.PARAMETER ArtifactDirectory
Directory containing corpus-results.csv plus per-dump logs and coverage folders.

.PARAMETER OutputCsv
Optional path for the corrected per-dump certification results.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactDirectory,

    [string] $OutputCsv,

    [string] $RepositoryRoot,

    [string] $DumpDirectory,

    [string] $DumpFilter = '*.dmp'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Read-RequiredText {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required corpus artifact is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw
}

function Read-RequiredCsv {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required corpus artifact is missing: $Path"
    }

    return @(Import-Csv -LiteralPath $Path)
}

function Read-RequiredJson {
    param([Parameter(Mandatory)] [string] $Path)

    return (Read-RequiredText $Path) | ConvertFrom-Json
}

function Read-ConversionEvents {
    param([Parameter(Mandatory)] [string] $Path)

    $events = [Collections.Generic.List[object]]::new()
    $lineNumber = 0
    foreach ($line in (Read-RequiredText $Path) -split '\r?\n') {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $event = $line | ConvertFrom-Json -ErrorAction Stop
        if ($event -isnot [pscustomobject]) {
            throw "Conversion event line $lineNumber is not a JSON object: $Path"
        }
        $available = @($event.PSObject.Properties.Name)
        if ($available -notcontains 'SchemaVersion' -or $available -notcontains 'Kind') {
            throw "Conversion event line $lineNumber has no schema envelope: $Path"
        }
        if ([int]$event.SchemaVersion -ne 1) {
            throw "Conversion event line $lineNumber uses unsupported schema version " +
                "'$($event.SchemaVersion)': $Path"
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
            default { throw "Conversion event line $lineNumber has unknown kind '$($event.Kind)': $Path" }
        }
        if (@($required | Where-Object { $available -notcontains $_ }).Count -ne 0) {
            throw "Conversion event line $lineNumber is missing required '$($event.Kind)' properties: $Path"
        }
        $events.Add($event)
    }

    if ($events.Count -eq 0) {
        throw "Conversion event log contains no events: $Path"
    }

    return $events.ToArray()
}

function Get-Sha256 {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Hash-bound input or output is missing: $Path"
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-Hash {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Expected,
        [Parameter(Mandatory)] [string] $Description
    )

    if ($Expected -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Description has no valid recorded SHA-256: $Expected"
    }

    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "$Description SHA-256 mismatch: expected $Expected, actual $actual ($Path)"
    }
}

function Assert-ExactNameSet {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Actual,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Expected,
        [Parameter(Mandatory)] [string] $Description
    )

    $actualDuplicates = @($Actual | Group-Object | Where-Object Count -ne 1)
    if ($actualDuplicates.Count -ne 0) {
        throw "$Description contains duplicate name(s): " +
            (($actualDuplicates | ForEach-Object Name) -join ', ')
    }

    $difference = @(Compare-Object ($Actual | Sort-Object) ($Expected | Sort-Object))
    if ($difference.Count -ne 0) {
        throw "$Description does not match the selected DMP set: " +
            (($difference | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ', ')
    }
}

function Assert-ToolSnapshot {
    param(
        [Parameter(Mandatory)] [object] $Metadata,
        [Parameter(Mandatory)] [string] $Description
    )

    Assert-Hash $Metadata.Path $Metadata.Sha256 "$Description entry assembly"
    $directory = Split-Path -Parent $Metadata.Path
    $expectedFiles = @($Metadata.Files)
    if ($expectedFiles.Count -eq 0) {
        throw "$Description has an empty toolchain file manifest."
    }

    $actualRelativePaths = @(
        Get-ChildItem -LiteralPath $directory -File -Recurse -Force |
            ForEach-Object { [IO.Path]::GetRelativePath($directory, $_.FullName) }
    )
    $expectedRelativePaths = @($expectedFiles | ForEach-Object RelativePath)
    Assert-ExactNameSet $actualRelativePaths $expectedRelativePaths "$Description toolchain snapshot"
    foreach ($file in $expectedFiles) {
        $path = Join-Path $directory $file.RelativePath
        if ((Get-Item -LiteralPath $path).Length -ne [int64]$file.Bytes) {
            throw "$Description byte-length mismatch: $($file.RelativePath)"
        }
        Assert-Hash $path $file.Sha256 "$Description file $($file.RelativePath)"
    }
}

function Get-SuppressedRecordIds {
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

function Test-SuppressionTelemetry {
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

function Get-DialogueIdentityTelemetry {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Events)

    $summary = @($Events | Where-Object {
        $_.Kind -eq 'Event' -and $_.Code -eq 'dialogue.identity.summary'
    })
    if ($summary.Count -ne 1) {
        return $null
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
        }).Count -ne 0 -or
        @($identities | Group-Object FormId | Where-Object Count -ne 1).Count -ne 0) {
        return $null
    }

    return [pscustomobject]@{
        MasterAnchors = @($identities | Where-Object Code -eq 'dialogue.identity.master-anchor').Count
        SharedChildAnchors = @($identities | Where-Object Code -eq 'dialogue.identity.shared-child-anchor').Count
        PrototypeDistinct = @($identities | Where-Object Code -eq 'dialogue.identity.prototype-distinct').Count
        Ambiguous = @($identities | Where-Object Code -eq 'dialogue.identity.ambiguous').Count
    }
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($DumpDirectory)) {
    $DumpDirectory = Join-Path $RepositoryRoot 'Sample\MemoryDump'
}
$DumpDirectory = (Resolve-Path -LiteralPath $DumpDirectory).Path
if ([string]::IsNullOrWhiteSpace($DumpFilter)) {
    throw 'DumpFilter cannot be empty.'
}

$expectedDumps = @(
    Get-ChildItem -LiteralPath $DumpDirectory -File -Filter $DumpFilter |
        Where-Object Extension -IEQ '.dmp' |
        Sort-Object Name
)
if ($expectedDumps.Count -eq 0) {
    throw "No .dmp files matched '$DumpFilter' beneath $DumpDirectory"
}

$root = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$manifestPath = Join-Path $root 'corpus-results.csv'
$manifest = Read-RequiredCsv $manifestPath
if ($manifest.Count -eq 0) {
    throw "Corpus manifest contains no rows: $manifestPath"
}

$buildInputs = Read-RequiredJson (Join-Path $root 'corpus-build-inputs.json')
if ([int]$buildInputs.SchemaVersion -ne 1) {
    throw "Unsupported corpus-build-inputs schema version: $($buildInputs.SchemaVersion)"
}
if ([IO.Path]::GetFullPath($buildInputs.DumpDirectory) -ne [IO.Path]::GetFullPath($DumpDirectory) -or
    $buildInputs.DumpFilter -ne $DumpFilter) {
    throw "Verifier DMP selection does not match corpus metadata: metadata='$($buildInputs.DumpDirectory)' " +
        "filter '$($buildInputs.DumpFilter)', verifier='$DumpDirectory' filter '$DumpFilter'."
}

$expectedNames = @($expectedDumps | ForEach-Object Name)
$manifestNames = @($manifest | ForEach-Object Dump)
$metadataNames = @($buildInputs.Dumps | ForEach-Object Name)
Assert-ExactNameSet $manifestNames $expectedNames 'Corpus manifest'
Assert-ExactNameSet $metadataNames $expectedNames 'Corpus build metadata'

Assert-ToolSnapshot $buildInputs.BethesdaMultitool 'BethesdaMultitool'
Assert-ToolSnapshot $buildInputs.EsmAnalyzer 'EsmAnalyzer'
Assert-Hash $buildInputs.MasterEsm.Path $buildInputs.MasterEsm.Sha256 'Master ESM'
Assert-Hash $buildInputs.JulyDialogueCsv.Path $buildInputs.JulyDialogueCsv.Sha256 'July dialogue CSV'
Assert-Hash $buildInputs.AprilDialogueCsv.Path $buildInputs.AprilDialogueCsv.Sha256 'April dialogue CSV'
Assert-Hash $buildInputs.CellAuthority.Path $buildInputs.CellAuthority.Sha256 'Cell authority'

$metadataDumpsByName = @{}
foreach ($metadataDump in $buildInputs.Dumps) {
    $metadataDumpsByName[$metadataDump.Name] = $metadataDump
}
foreach ($dump in $expectedDumps) {
    $metadataDump = $metadataDumpsByName[$dump.Name]
    if ([int64]$metadataDump.Bytes -ne $dump.Length) {
        throw "DMP byte-length mismatch for $($dump.Name): metadata=$($metadataDump.Bytes), actual=$($dump.Length)"
    }
    Assert-Hash $dump.FullName $metadataDump.Sha256 "DMP $($dump.Name)"
}

$results = foreach ($row in $manifest) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($row.Dump)
    $buildRaw = Read-RequiredText (Join-Path $root "$stem.build.log")
    $build = $buildRaw -replace '\s+', ' '
    $events = @(Read-ConversionEvents (Join-Path $root "$stem.events.jsonl"))
    $deep = Read-RequiredText (Join-Path $root "$stem.deep.log")
    $dialogue = Read-RequiredText (Join-Path $root "$stem.dialogue.log")
    $coverageLog = (Read-RequiredText (Join-Path $root "$stem.coverage.log")) -replace '\s+', ' '
    $coverageDirectory = Join-Path $root "$stem.coverage"

    # PowerShell unwraps a function's single pipeline result even when that
    # function constructs an array internally.  Keep these collection-shaped
    # so one-row coverage files behave exactly like larger corpus members.
    $records = @(Read-RequiredCsv (Join-Path $coverageDirectory 'record_coverage.csv'))
    $subrecords = @(Read-RequiredCsv (Join-Path $coverageDirectory 'subrecord_coverage.csv'))
    $scripts = @(Read-RequiredCsv (Join-Path $coverageDirectory 'script_bytecode_coverage.csv'))

    $metadataDump = $metadataDumpsByName[$row.Dump]
    $hashBindingsOk =
        $row.InputSha256 -eq $metadataDump.Sha256 -and
        $row.BethesdaMultitoolSha256 -eq $buildInputs.BethesdaMultitool.Sha256 -and
        $row.EsmAnalyzerSha256 -eq $buildInputs.EsmAnalyzer.Sha256 -and
        $row.MasterSha256 -eq $buildInputs.MasterEsm.Sha256 -and
        $row.JulyDialogueCsvSha256 -eq $buildInputs.JulyDialogueCsv.Sha256 -and
        $row.AprilDialogueCsvSha256 -eq $buildInputs.AprilDialogueCsv.Sha256 -and
        $row.CellAuthoritySha256 -eq $buildInputs.CellAuthority.Sha256
    $outputOk = $false
    $expectedOutput = Join-Path $root "$stem.$($buildInputs.OutputTag).esm"
    $outputPathMatches = try {
        [IO.Path]::GetFullPath($row.Output) -eq [IO.Path]::GetFullPath($expectedOutput)
    }
    catch {
        $false
    }
    if ($outputPathMatches -and (Test-Path -LiteralPath $row.Output -PathType Leaf)) {
        $outputItem = Get-Item -LiteralPath $row.Output
        $outputOk =
            $row.OutputSha256 -match '^[0-9A-Fa-f]{64}$' -and
            [int64]$row.OutputBytes -eq $outputItem.Length -and
            (Get-Sha256 $row.Output) -eq $row.OutputSha256.ToUpperInvariant()
    }

    $info = Get-DialogueTableCount $dialogue 'INFO (Dialogue) records'
    $linked = Get-DialogueTableCount $dialogue 'Linked to topic'
    $unlinked = Get-DialogueTableCount $dialogue 'Unlinked'
    $responses = Get-DialogueTableCount $dialogue 'With response text'

    $unparsedRows = @($records | Where-Object classification -eq 'Unparsed')
    $unparsed = if ($unparsedRows.Count -eq 0) {
        0L
    }
    else {
        [int64](($unparsedRows | Measure-Object -Property count -Sum).Sum)
    }

    $rawGaps = @(
        $subrecords | Where-Object {
            $_.uses_raw_byte_array -eq 'True' -and
            $_.is_intentional_raw -ne 'True'
        }
    ).Count

    $hardScriptIssues = @(
        $scripts | Where-Object {
            $_.compiled_size_matches -ne 'True' -or
            $_.ref_count_matches -ne 'True' -or
            $_.walked_to_end -ne 'True' -or
            $_.has_diagnostics -eq 'True'
        }
    ).Count
    $scdaRows = @(
        $subrecords | Where-Object {
            $_.subrecord -eq 'SCDA'
        }
    )
    $scdaCount = if ($scdaRows.Count -eq 0) {
        0L
    }
    else {
        [int64](($scdaRows | Measure-Object -Property count -Sum).Sum)
    }
    $scriptCoverageComplete = $scripts.Count -eq $scdaCount
    $variableTelemetry = @(
        $scripts | Where-Object variable_count_matches -ne 'True'
    ).Count
    $suppressedInfoIds = @(Get-SuppressedRecordIds $events 'INFO')
    $suppressedPackageIds = @(Get-SuppressedRecordIds $events 'PACK')
    $suppressedScriptIds = @(Get-SuppressedRecordIds $events 'SCPT')
    $dialogueIdentity = Get-DialogueIdentityTelemetry $events
    $completeEvents = @($events | Where-Object Kind -eq 'Complete')
    $eventLogOk =
        $completeEvents.Count -eq 1 -and
        [int]$completeEvents[0].RecordsFailed -eq 0 -and
        [int]$completeEvents[0].Errors -eq 0 -and
        (Test-SuppressionTelemetry $events)

    $conversionOk =
        $row.ConvertExit -eq '0' -and
        $build -match 'Conversion succeeded\.' -and
        $build -match 'Re-parser read .* record\(s\) without errors\.' -and
        $build -match 'Semantic check passed:' -and
        $eventLogOk -and
        $null -ne $dialogueIdentity

    $deepOk = $row.DeepExit -eq '0' -and $deep -match 'Deep validation OK'
    $dialogueOk =
        $row.DialogueExit -eq '0' -and
        $null -ne $info -and
        $null -ne $linked -and
        $null -ne $unlinked -and
        $null -ne $responses -and
        $unlinked -eq 0 -and
        ($linked + $unlinked) -eq $info -and
        $responses -le $info

    $coverageOk =
        $row.CoverageExit -eq '0' -and
        $unparsed -eq 0 -and
        $rawGaps -eq 0

    $overall =
        $row.Success -eq 'True' -and
        $hashBindingsOk -and
        $outputOk -and
        $conversionOk -and
        $deepOk -and
        $dialogueOk -and
        $coverageOk -and
        $scriptCoverageComplete -and
        $hardScriptIssues -eq 0 -and
        [int]$row.TypeWarningLines -eq 0

    $failures = @(
        if ($row.Success -ne 'True') { 'runner-manifest-failed' }
        if (-not $hashBindingsOk) { 'input-hash-binding' }
        if (-not $outputOk) { 'output-hash-binding' }
        if (-not $conversionOk) { 'conversion/reparse/semantic' }
        if (-not $deepOk) { 'deep-validation' }
        if (-not $dialogueOk) { 'dialogue-linkage' }
        if (-not $coverageOk) { 'coverage' }
        if (-not $scriptCoverageComplete) { "script-coverage:$($scripts.Count)/$scdaCount" }
        if ($hardScriptIssues -ne 0) { "script-integrity:$hardScriptIssues" }
        if ([int]$row.TypeWarningLines -ne 0) { "type-warnings:$($row.TypeWarningLines)" }
    )

    [pscustomobject]@{
        Dump = $row.Dump
        Overall = $overall
        Conversion = $conversionOk
        Deep = $deepOk
        Dialogue = $dialogueOk
        Coverage = $coverageOk
        HashBindings = $hashBindingsOk
        OutputHash = $outputOk
        ScriptCoverageComplete = $scriptCoverageComplete
        ScdaBlocks = $scdaCount
        ScriptCoverageRows = $scripts.Count
        HardScriptIssues = $hardScriptIssues
        VariableTelemetry = $variableTelemetry
        InfoCount = $info
        SilentInfoCount = if ($null -ne $info -and $null -ne $responses) { $info - $responses } else { $null }
        SuppressedInfos = $suppressedInfoIds.Count
        SuppressedPackages = $suppressedPackageIds.Count
        SuppressedScripts = $suppressedScriptIds.Count
        DialogueMasterAnchors = if ($null -ne $dialogueIdentity) { $dialogueIdentity.MasterAnchors } else { $null }
        DialogueSharedChildAnchors = if ($null -ne $dialogueIdentity) { $dialogueIdentity.SharedChildAnchors } else { $null }
        DialoguePrototypeDistinct = if ($null -ne $dialogueIdentity) { $dialogueIdentity.PrototypeDistinct } else { $null }
        DialogueAmbiguous = if ($null -ne $dialogueIdentity) { $dialogueIdentity.Ambiguous } else { $null }
        SuppressedInfoFormIds = $suppressedInfoIds -join ';'
        SuppressedPackageFormIds = $suppressedPackageIds -join ';'
        SuppressedScriptFormIds = $suppressedScriptIds -join ';'
        Failures = $failures -join ';'
    }
}

if ($OutputCsv) {
    $outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputCsv)
    $outputParent = Split-Path -Parent $outputPath
    if ($outputParent -and -not (Test-Path -LiteralPath $outputParent)) {
        New-Item -ItemType Directory -Path $outputParent | Out-Null
    }

    $results | Export-Csv -LiteralPath $outputPath -NoTypeInformation
}

$passed = @($results | Where-Object Overall).Count
$failed = @($results | Where-Object { -not $_.Overall }).Count
$results | Format-Table Dump, Overall, Conversion, Deep, Dialogue, Coverage,
    HardScriptIssues, VariableTelemetry, SuppressedInfos, SuppressedPackages, SuppressedScripts,
    DialogueSharedChildAnchors, DialogueAmbiguous -AutoSize | Out-Host
Write-Host "Corpus certification: $passed/$($results.Count) passed; $failed failed."
$distinctSuppressedInfos = @(
    $results.SuppressedInfoFormIds -split ';' |
        Where-Object { $_ } |
        Sort-Object -Unique
).Count
$distinctSuppressedPackages = @(
    $results.SuppressedPackageFormIds -split ';' |
        Where-Object { $_ } |
        Sort-Object -Unique
).Count
$distinctSuppressedScripts = @(
    $results.SuppressedScriptFormIds -split ';' |
        Where-Object { $_ } |
        Sort-Object -Unique
).Count
Write-Host (
    "Fail-closed telemetry (per-dump unique occurrences): {0:N0} INFO, {1:N0} PACK, and {2:N0} SCPT." -f
    (($results | Measure-Object -Property SuppressedInfos -Sum).Sum),
    (($results | Measure-Object -Property SuppressedPackages -Sum).Sum),
    (($results | Measure-Object -Property SuppressedScripts -Sum).Sum)
)
Write-Host (
    ("Dialogue identity telemetry: {0:N0} exact master anchors, {1:N0} shared-child anchors, " +
     "{2:N0} prototype-distinct, and {3:N0} ambiguous classifications.") -f
    (($results | Measure-Object -Property DialogueMasterAnchors -Sum).Sum),
    (($results | Measure-Object -Property DialogueSharedChildAnchors -Sum).Sum),
    (($results | Measure-Object -Property DialoguePrototypeDistinct -Sum).Sum),
    (($results | Measure-Object -Property DialogueAmbiguous -Sum).Sum)
)
Write-Host (
    (("Distinct logged FormIDs across all dumps: {0:N0} INFO, {1:N0} PACK, and {2:N0} SCPT " +
      "(diagnostic only; allocated IDs may be reused across builds).") -f
        $distinctSuppressedInfos,
        $distinctSuppressedPackages,
        $distinctSuppressedScripts)
)

if ($failed -ne 0) {
    $results | Where-Object { -not $_.Overall } |
        Format-Table Dump, Failures -AutoSize | Out-Host
    exit 1
}

exit 0
