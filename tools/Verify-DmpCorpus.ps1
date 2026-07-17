<#
.SYNOPSIS
Certifies an existing full-DMP conversion artifact directory.

.DESCRIPTION
Evaluates conversion, reparse, semantic, deep-validation, dialogue-linkage,
structural dialogue-identity telemetry, record/subrecord coverage, same-dump
script auditing, emitted SCTX/SCDA payload identity, and compiled-script
integrity for every row in corpus-results.csv. It rejects a
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

function Test-ObjectProperties {
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

function Get-NumericPropertySum {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Rows,
        [Parameter(Mandatory)] [string] $Property
    )

    if ($null -eq $Rows -or $Rows.Count -eq 0) {
        return [int64]0
    }

    $measurement = $Rows | Measure-Object -Property $Property -Sum
    if ($null -eq $measurement -or $null -eq $measurement.Sum) {
        return [int64]0
    }

    return [int64]$measurement.Sum
}

function Get-PayloadCoverageSummary {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Rows,
        [Parameter(Mandatory)] [ValidateSet('Subrecord', 'Source')] [string] $ReportKind,
        [Parameter(Mandatory)] [ValidateSet('SCDA', 'SCTX')] [string] $Signature
    )

    $counts = @{}
    $bytes = @{}
    foreach ($row in $Rows) {
        if ($ReportKind -eq 'Subrecord') {
            if ($row.subrecord -ne $Signature) {
                continue
            }
            $count = [int64]$row.count
            $length = [int64]$row.data_length * $count
        }
        else {
            $prefix = $Signature.ToLowerInvariant()
            $count = [int64]$row."${prefix}_count"
            $lengthColumn = if ($Signature -eq 'SCTX') { 'sctx_raw_length' } else { 'scda_length' }
            $length = [int64]$row.$lengthColumn
        }

        if ($count -eq 0 -and $length -eq 0) {
            continue
        }
        $recordType = [string]$row.record_type
        $counts[$recordType] = [int64]($counts[$recordType] ?? 0L) + $count
        $bytes[$recordType] = [int64]($bytes[$recordType] ?? 0L) + $length
    }

    return (($counts.Keys | Sort-Object | ForEach-Object {
        '{0}={1}/{2}' -f $_, $counts[$_], $bytes[$_]
    }) -join ';')
}

function Test-PayloadHashList {
    param(
        [Parameter(Mandatory)] [int64] $Count,
        [AllowEmptyString()] [string] $Hashes
    )

    if ($Count -eq 0) {
        return [string]::IsNullOrWhiteSpace($Hashes)
    }
    if ($Count -lt 0 -or [string]::IsNullOrWhiteSpace($Hashes)) {
        return $false
    }

    $parts = @($Hashes -split '\|')
    return $parts.Count -eq $Count -and
        @($parts | Where-Object { $_ -notmatch '^[0-9A-Fa-f]{64}$' }).Count -eq 0
}

function Get-ScriptSourceCoverageAssessment {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $SourceRows,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $SubrecordRows
    )

    $hardContradictions = @(
        $SourceRows | Where-Object { $_.hard_contradiction -eq 'True' }
    ).Count
    $hashIntegrity = @($SourceRows | Where-Object {
        $sctxCount = [int64]$_.sctx_count
        $scdaCount = [int64]$_.scda_count
        $sctxRawLength = [int64]$_.sctx_raw_length
        $sctxDecodedLength = [int64]$_.sctx_decoded_length
        $scdaLength = [int64]$_.scda_length
        $sctxPresent = $_.sctx_present -eq 'True'
        $scdaPresent = $_.scda_present -eq 'True'
        $sctxCount -lt 0 -or
        $scdaCount -lt 0 -or
        $sctxRawLength -lt 0 -or
        $sctxDecodedLength -lt 0 -or
        $sctxDecodedLength -gt $sctxRawLength -or
        $scdaLength -lt 0 -or
        ($sctxCount -eq 0 -and $sctxRawLength -ne 0) -or
        ($scdaCount -eq 0 -and $scdaLength -ne 0) -or
        $sctxPresent -ne ($sctxCount -gt 0) -or
        $scdaPresent -ne ($scdaCount -gt 0) -or
        -not (Test-PayloadHashList $sctxCount ([string]$_.sctx_sha256)) -or
        -not (Test-PayloadHashList $scdaCount ([string]$_.scda_sha256))
    }).Count -eq 0
    $subrecordIntegrity = @($SubrecordRows | Where-Object {
        $_.subrecord -in @('SCDA', 'SCTX') -and
        ([int64]$_.count -lt 0 -or [int64]$_.data_length -lt 0)
    }).Count -eq 0
    $hashIntegrity = $hashIntegrity -and $subrecordIntegrity

    $sourceSctx = Get-PayloadCoverageSummary $SourceRows 'Source' 'SCTX'
    $subrecordSctx = Get-PayloadCoverageSummary $SubrecordRows 'Subrecord' 'SCTX'
    $sourceScda = Get-PayloadCoverageSummary $SourceRows 'Source' 'SCDA'
    $subrecordScda = Get-PayloadCoverageSummary $SubrecordRows 'Subrecord' 'SCDA'
    $sctxCount = Get-NumericPropertySum -Rows $SourceRows -Property 'sctx_count'

    $result = [pscustomobject]@{
        HardContradictions = $hardContradictions
        HashIntegrity = $hashIntegrity
        SctxCount = $sctxCount
        SctxReconciled = $sourceSctx -ceq $subrecordSctx
        ScdaReconciled = $sourceScda -ceq $subrecordScda
    }
    $result | Add-Member -NotePropertyName Pass -NotePropertyValue (
        $result.HardContradictions -eq 0 -and
        $result.HashIntegrity -and
        $result.SctxReconciled -and
        $result.ScdaReconciled)
    return $result
}

function Test-ScriptSourceProof {
    param([Parameter(Mandatory)] [object] $Metadata)

    $shaPattern = '^[0-9A-Fa-f]{64}$'
    $actualHash = [string]$Metadata.'sctx-sha256'
    $expectedHash = [string]$Metadata.'expected-sctx-sha256'
    $baseHash = [string]$Metadata.'base-sctx-sha256'
    if ($actualHash -notmatch $shaPattern -or
        $expectedHash -notmatch $shaPattern -or
        $expectedHash -ine $actualHash) {
        return $false
    }

    $countText = [string]$Metadata.'augmentation-declaration-count'
    if ($countText -notmatch '^\d+$') {
        return $false
    }
    try {
        $declarationCount = [int]$countText
    }
    catch {
        return $false
    }

    $origin = [string]$Metadata.'source-origin'
    $proofKind = [string]$Metadata.'sctx-proof-kind'
    $capturedSourceHash = [string]$Metadata.'captured-source-utf8-sha256'
    $declarationBase64 = [string]$Metadata.'augmentation-declarations-base64'
    $declarationHash = [string]$Metadata.'augmentation-declarations-sha256'
    if ($origin -ne 'augmentation') {
        return $proofKind -eq 'captured-exact' -and
            $baseHash -ieq $expectedHash -and
            $capturedSourceHash -match $shaPattern -and
            $declarationCount -eq 0 -and
            [string]::IsNullOrWhiteSpace($declarationBase64) -and
            [string]::IsNullOrWhiteSpace($declarationHash)
    }

    if ($proofKind -eq 'master-executable-source') {
        return $baseHash -ieq $expectedHash -and
            [string]::IsNullOrWhiteSpace($capturedSourceHash) -and
            $declarationCount -eq 0 -and
            [string]::IsNullOrWhiteSpace($declarationBase64) -and
            [string]::IsNullOrWhiteSpace($declarationHash)
    }

    if ($proofKind -ne 'master-plus-declarations' -or
        $baseHash -notmatch $shaPattern -or
        -not [string]::IsNullOrWhiteSpace($capturedSourceHash) -or
        $declarationCount -le 0 -or
        [string]::IsNullOrWhiteSpace($declarationBase64) -or
        $declarationHash -notmatch $shaPattern) {
        return $false
    }

    try {
        $declarationBytes = [Convert]::FromBase64String($declarationBase64)
    }
    catch {
        return $false
    }
    if ($declarationBytes.Length -eq 0 -or
        $declarationBytes[-1] -in @(0x0A, 0x0D) -or
        @($declarationBytes | Where-Object { $_ -eq 0 }).Count -ne 0 -or
        (1 + @($declarationBytes | Where-Object { $_ -eq 0x0A }).Count) -ne $declarationCount) {
        return $false
    }

    $actualDeclarationHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($declarationBytes))
    return $actualDeclarationHash -ieq $declarationHash
}

function Test-ScriptSemanticProof {
    param([Parameter(Mandatory)] $Metadata)

    $origin = [string]$Metadata.'source-origin'
    $semantic = [string]$Metadata.'sctx-scda-semantic-match'
    $scdaLengthText = [string]$Metadata.'scda-length'
    $matchCount = [string]$Metadata.'sctx-scda-match-count'
    $toleratedCount = [string]$Metadata.'sctx-scda-tolerated-count'
    $toleratedCategories = [string]$Metadata.'sctx-scda-tolerated-categories'
    if ($scdaLengthText -notmatch '^\d+$') {
        return $false
    }

    if ($origin -eq 'augmentation') {
        $proofKind = [string]$Metadata.'sctx-proof-kind'
        $expectedSemantic = if ($proofKind -eq 'master-executable-source') {
            'master-executable-source-augmented-local-table-incomplete'
        } else {
            'master-base-plus-fresh-local-declarations'
        }
        return $semantic -eq $expectedSemantic -and
            [string]::IsNullOrWhiteSpace($matchCount) -and
            [string]::IsNullOrWhiteSpace($toleratedCount) -and
            [string]::IsNullOrWhiteSpace($toleratedCategories)
    }

    if ([int64]$scdaLengthText -eq 0) {
        return $semantic -eq 'source-only-no-scda' -and
            [string]::IsNullOrWhiteSpace($matchCount) -and
            [string]::IsNullOrWhiteSpace($toleratedCount) -and
            [string]::IsNullOrWhiteSpace($toleratedCategories)
    }

    return $semantic -eq 'proven-zero-nontolerated-mismatches' -and
        $matchCount -match '^\d+$' -and
        $toleratedCount -match '^\d+$'
}

function Get-ScriptProvenanceAssessment {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Events,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $SourceRows,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $AuditRows
    )

    $provenance = @($Events | Where-Object {
        $_.Kind -eq 'Event' -and $_.Code -eq 'script.source-provenance'
    })
    $requiredMetadata = @(
        'source-form-id', 'emitted-form-id', 'editor-id', 'source-origin',
        'sctx-proof-kind', 'sctx-sha256', 'expected-sctx-sha256', 'base-sctx-sha256',
        'captured-source-utf8-sha256',
        'augmentation-declaration-count', 'augmentation-declarations-base64',
        'augmentation-declarations-sha256',
        'sctx-decoded-length', 'scda-sha256', 'scda-length',
        'bytecode-changed-from-source', 'tables-changed-from-source',
        'sctx-scda-semantic-match', 'sctx-scda-match-count',
        'sctx-scda-tolerated-count', 'sctx-scda-tolerated-categories'
    )
    $validOrigins = @('runtime-same-object', 'dmp-fragment', 'augmentation')
    $matched = 0
    $valid = $true
    $seen = @{}
    $expectedRows = @($SourceRows | Where-Object {
        $_.record_type -eq 'SCPT' -and $_.sctx_present -eq 'True'
    })
    $expected = @{}
    foreach ($coverage in $expectedRows) {
        $formId = ([string]$coverage.form_id).ToUpperInvariant()
        if ($formId -notmatch '^0x[0-9A-Fa-f]{8}$' -or $expected.ContainsKey($formId)) {
            $valid = $false
            continue
        }
        $expected[$formId] = $coverage
    }

    foreach ($event in $provenance) {
        $metadata = $event.Metadata
        if ($event.FormType -ne 'SCPT' -or
            $event.FormId -notmatch '^0x[0-9A-Fa-f]{8}$' -or
            -not (Test-ObjectProperties $metadata $requiredMetadata)) {
            $valid = $false
            continue
        }

        $formId = $event.FormId.ToUpperInvariant()
        if ($seen.ContainsKey($formId)) {
            $valid = $false
            continue
        }
        $seen[$formId] = $true

        $sourceFormId = [string]$metadata.'source-form-id'
        if ($metadata.'emitted-form-id' -ne $formId -or
            (-not [string]::IsNullOrWhiteSpace($sourceFormId) -and
             $sourceFormId -notmatch '^0x[0-9A-Fa-f]{8}$') -or
            $validOrigins -notcontains $metadata.'source-origin' -or
            ($metadata.'source-origin' -eq 'augmentation' -and
             -not [string]::IsNullOrWhiteSpace($sourceFormId)) -or
            ($metadata.'source-origin' -ne 'augmentation' -and
             [string]::IsNullOrWhiteSpace($sourceFormId)) -or
            -not (Test-ScriptSourceProof $metadata) -or
            [string]$metadata.'sctx-decoded-length' -notmatch '^\d+$' -or
            [string]$metadata.'scda-length' -notmatch '^\d+$' -or
            $metadata.'bytecode-changed-from-source' -notin @('true', 'false') -or
            $metadata.'tables-changed-from-source' -notin @('true', 'false') -or
            -not (Test-ScriptSemanticProof $metadata)) {
            $valid = $false
            continue
        }

        if ($metadata.'source-origin' -ne 'augmentation') {
            $sourceKey = $sourceFormId.ToUpperInvariant()
            $capturedSourceHash = [string]$metadata.'captured-source-utf8-sha256'
            $mergedAuditMatches = @($AuditRows | Where-Object {
                $_.row_kind -eq 'merged' -and
                ([string]$_.form_id).ToUpperInvariant() -eq $sourceKey -and
                [string]$_.source_sha256 -ieq $capturedSourceHash
            })
            $runtimeAuditMatches = @($AuditRows | Where-Object {
                $_.row_kind -eq 'runtime' -and
                ([string]$_.form_id).ToUpperInvariant() -eq $sourceKey -and
                [string]$_.source_sha256 -ieq $capturedSourceHash
            })
            if ($mergedAuditMatches.Count -eq 0 -or
                ($metadata.'source-origin' -eq 'runtime-same-object' -and
                 $runtimeAuditMatches.Count -eq 0)) {
                $valid = $false
                continue
            }

            $semantic = [string]$metadata.'sctx-scda-semantic-match'
            $correspondenceMatches = if ($semantic -eq 'source-only-no-scda') {
                @($mergedAuditMatches | Where-Object {
                    $_.content_classification -eq 'source-only' -and
                    [int64]$_.scda_length -eq 0 -and
                    [int64]$_.effective_variable_count -eq 0 -and
                    [int64]$_.variable_list_count -eq 0 -and
                    [int64]$_.reference_list_count -eq 0 -and
                    [string]::IsNullOrWhiteSpace([string]$_.structural_diagnostics) -and
                    [string]::IsNullOrWhiteSpace([string]$_.hard_contradictions)
                })
            }
            else {
                @($mergedAuditMatches | Where-Object {
                    $_.content_classification -eq 'both' -and
                    $_.declaration_identity_verdict -eq 'exact' -and
                    $_.declared_scda_length_matches -eq 'true' -and
                    $_.header_variable_count_matches_list -eq 'true' -and
                    $_.effective_variable_count_matches_list -eq 'true' -and
                    $_.header_reference_count_matches_list -eq 'true' -and
                    [string]::IsNullOrWhiteSpace([string]$_.structural_diagnostics) -and
                    [string]::IsNullOrWhiteSpace([string]$_.hard_contradictions) -and
                    [int64]$_.comparison_mismatch_count -eq 0 -and
                    [int64]$_.comparison_match_count -eq [int64]$metadata.'sctx-scda-match-count' -and
                    [int64]$_.comparison_tolerated_count -eq [int64]$metadata.'sctx-scda-tolerated-count' -and
                    [string]$_.comparison_tolerated_categories -ceq [string]$metadata.'sctx-scda-tolerated-categories'
                })
            }
            if ($correspondenceMatches.Count -eq 0) {
                $valid = $false
                continue
            }
        }

        $coverageRows = @($SourceRows | Where-Object {
            $_.record_type -eq 'SCPT' -and
            ([string]$_.form_id).ToUpperInvariant() -eq $formId -and
            $_.sctx_present -eq 'True'
        })
        if ($coverageRows.Count -ne 1) {
            $valid = $false
            continue
        }

        $coverage = $coverageRows[0]
        $scdaCount = [int64]$coverage.scda_count
        $eventScdaHash = [string]$metadata.'scda-sha256'
        $scdaMatches = if ($scdaCount -eq 0) {
            [string]::IsNullOrWhiteSpace($eventScdaHash)
        }
        else {
            $scdaCount -eq 1 -and
            $eventScdaHash -match '^[0-9A-Fa-f]{64}$' -and
            $eventScdaHash -ieq [string]$coverage.scda_sha256
        }
        if ([int64]$coverage.sctx_count -ne 1 -or
            $metadata.'sctx-sha256' -ine [string]$coverage.sctx_sha256 -or
            [int64]$metadata.'sctx-decoded-length' -ne [int64]$coverage.sctx_decoded_length -or
            [int64]$metadata.'scda-length' -ne [int64]$coverage.scda_length -or
            -not $scdaMatches) {
            $valid = $false
            continue
        }

        $matched++
    }

    $setEqual = $seen.Count -eq $expected.Count -and
        @($expected.Keys | Where-Object { -not $seen.ContainsKey($_) }).Count -eq 0

    return [pscustomobject]@{
        EventCount = $provenance.Count
        ExpectedCount = $expectedRows.Count
        MatchedCount = $matched
        Pass = $valid -and
            $matched -eq $provenance.Count -and
            $matched -eq $expectedRows.Count -and
            $setEqual
    }
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
        [Parameter(Mandatory)] [ValidateSet('INFO', 'PACK', 'SCPT', 'TERM')] [string] $RecordType
    )

    $codes = switch ($RecordType) {
        'INFO' { @('quest-variable.record-suppressed', 'quest-variable.record-suppressed-no-emitted-producer', 'script-variable.record-suppressed', 'script-variable.owner-not-emitted', 'inline-script.suppress-unsafe-owner') }
        'PACK' { @('quest-variable.record-suppressed', 'quest-variable.record-suppressed-no-emitted-producer', 'script-variable.record-suppressed', 'inline-script.suppress-unsafe-owner') }
        'SCPT' { @('script.suppress-unsafe-reference-table', 'script.suppress-post-verdict-reference-table') }
        'TERM' { @('quest-variable.menu-item-suppressed', 'quest-variable.menu-item-suppressed-no-emitted-producer', 'script-variable.menu-item-suppressed', 'inline-script.suppress-unsafe-owner') }
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

    $inlineScriptCode = 'inline-script.suppress-unsafe-owner'
    $questVariableCodes = @(
        'quest-variable.record-suppressed',
        'quest-variable.record-suppressed-no-emitted-producer'
    )
    $scriptVariableCodes = @(
        'script-variable.record-suppressed',
        'script-variable.owner-not-emitted'
    )
    $terminalMenuCodes = @(
        'quest-variable.menu-item-suppressed',
        'quest-variable.menu-item-suppressed-no-emitted-producer',
        'script-variable.menu-item-suppressed'
    )
    $scriptCodes = @(
        'script.suppress-unsafe-reference-table',
        'script.suppress-post-verdict-reference-table'
    )
    foreach ($event in $Events | Where-Object {
        $_.Kind -eq 'Event' -and
        ($questVariableCodes -contains $_.Code -or
         $scriptVariableCodes -contains $_.Code -or
         $terminalMenuCodes -contains $_.Code -or
         $_.Code -eq $inlineScriptCode -or
         $scriptCodes -contains $_.Code)
    }) {
        if ($event.FormId -notmatch '^0x[0-9A-Fa-f]{8}$') {
            return $false
        }
        if ($scriptCodes -contains $event.Code) {
            if ($event.FormType -ne 'SCPT') {
                return $false
            }
        }
        elseif ($event.Code -eq $inlineScriptCode) {
            if ($event.FormType -notin @('INFO', 'PACK', 'TERM')) {
                return $false
            }
        }
        elseif ($terminalMenuCodes -contains $event.Code) {
            if ($event.FormType -ne 'TERM') {
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
    $scriptAuditLog = Read-RequiredText (Join-Path $root "$stem.script-audit.log")
    $scriptAuditPath = Join-Path $root "$stem.script-audit.csv"
    $coverageDirectory = Join-Path $root "$stem.coverage"

    # PowerShell unwraps a function's single pipeline result even when that
    # function constructs an array internally.  Keep these collection-shaped
    # so one-row coverage files behave exactly like larger corpus members.
    $records = @(Read-RequiredCsv (Join-Path $coverageDirectory 'record_coverage.csv'))
    $subrecords = @(Read-RequiredCsv (Join-Path $coverageDirectory 'subrecord_coverage.csv'))
    $scripts = @(Read-RequiredCsv (Join-Path $coverageDirectory 'script_bytecode_coverage.csv'))
    $scriptSources = @(Read-RequiredCsv (Join-Path $coverageDirectory 'script_source_coverage.csv'))
    $scriptAuditRows = @(Read-RequiredCsv $scriptAuditPath)

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
    $unparsed = Get-NumericPropertySum -Rows $unparsedRows -Property 'count'

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
    $scdaCount = Get-NumericPropertySum -Rows $scdaRows -Property 'count'
    $scriptCoverageComplete = $scripts.Count -eq $scdaCount
    $scriptAuditHardContradictions = @(
        $scriptAuditRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.hard_contradictions) }
    ).Count
    $scriptAuditIntegrity = @($scriptAuditRows | Where-Object {
        $_.row_kind -notin @('runtime', 'merged') -or
        $_.form_id -notmatch '^0x[0-9A-Fa-f]{8}$' -or
        ([int64]$_.source_utf8_length -gt 0 -and $_.source_sha256 -notmatch '^[0-9A-Fa-f]{64}$') -or
        ([int64]$_.scda_length -gt 0 -and $_.scda_sha256 -notmatch '^[0-9A-Fa-f]{64}$')
    }).Count -eq 0
    $scriptAuditOk =
        $row.ScriptAuditExit -eq '0' -and
        $scriptAuditHardContradictions -eq 0 -and
        $scriptAuditIntegrity -and
        [int]$row.ScriptAuditRows -eq $scriptAuditRows.Count -and
        [int]$row.ScriptAuditHardContradictions -eq $scriptAuditHardContradictions -and
        $row.ScriptAuditPass -eq 'True' -and
        $row.ScriptAuditSha256 -match '^[0-9A-Fa-f]{64}$' -and
        (Get-Sha256 $scriptAuditPath) -eq $row.ScriptAuditSha256.ToUpperInvariant()

    $scriptSourcePath = Join-Path $coverageDirectory 'script_source_coverage.csv'
    $scriptSourceAssessment = Get-ScriptSourceCoverageAssessment $scriptSources $subrecords
    $scriptProvenanceAssessment = Get-ScriptProvenanceAssessment `
        $events $scriptSources $scriptAuditRows
    $scriptSourceOk =
        $scriptSourceAssessment.Pass -and
        [int]$row.ScriptSourceCoverageRows -eq $scriptSources.Count -and
        [int]$row.ScriptSourceHardContradictions -eq $scriptSourceAssessment.HardContradictions -and
        [int64]$row.ScriptSourceSctxBlocks -eq $scriptSourceAssessment.SctxCount -and
        $row.ScriptSourceSctxReconciled -eq [string]$scriptSourceAssessment.SctxReconciled -and
        $row.ScriptSourceScdaReconciled -eq [string]$scriptSourceAssessment.ScdaReconciled -and
        $row.ScriptSourceHashIntegrity -eq [string]$scriptSourceAssessment.HashIntegrity -and
        $row.ScriptSourceCoveragePass -eq 'True' -and
        $row.ScriptSourceCoverageSha256 -match '^[0-9A-Fa-f]{64}$' -and
        (Get-Sha256 $scriptSourcePath) -eq $row.ScriptSourceCoverageSha256.ToUpperInvariant()
    $scriptProvenanceOk =
        $scriptProvenanceAssessment.Pass -and
        [int]$row.ScriptProvenanceEvents -eq $scriptProvenanceAssessment.EventCount -and
        [int]$row.ScriptProvenanceMatched -eq $scriptProvenanceAssessment.MatchedCount -and
        $row.ScriptProvenancePass -eq 'True'
    $variableTelemetry = @(
        $scripts | Where-Object variable_count_matches -ne 'True'
    ).Count
    $suppressedInfoIds = @(Get-SuppressedRecordIds $events 'INFO')
    $suppressedPackageIds = @(Get-SuppressedRecordIds $events 'PACK')
    $suppressedScriptIds = @(Get-SuppressedRecordIds $events 'SCPT')
    $suppressedTerminalIds = @(Get-SuppressedRecordIds $events 'TERM')
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
        $scriptAuditOk -and
        $scriptSourceOk -and
        $scriptProvenanceOk -and
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
        if (-not $scriptAuditOk) { "same-dump-script-audit:$scriptAuditHardContradictions" }
        if (-not $scriptSourceOk) { "script-source-coverage:$($scriptSourceAssessment.HardContradictions)" }
        if (-not $scriptProvenanceOk) {
            "script-source-provenance:$($scriptProvenanceAssessment.MatchedCount)/$($scriptProvenanceAssessment.ExpectedCount) expected;events=$($scriptProvenanceAssessment.EventCount)"
        }
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
        ScriptAudit = $scriptAuditOk
        ScriptAuditRows = $scriptAuditRows.Count
        ScriptAuditHardContradictions = $scriptAuditHardContradictions
        ScriptSourceCoverage = $scriptSourceOk
        ScriptSourceRows = $scriptSources.Count
        ScriptSourceHardContradictions = $scriptSourceAssessment.HardContradictions
        ScriptSourceSctxBlocks = $scriptSourceAssessment.SctxCount
        ScriptProvenance = $scriptProvenanceOk
        ScriptProvenanceEvents = $scriptProvenanceAssessment.EventCount
        ScriptProvenanceMatched = $scriptProvenanceAssessment.MatchedCount
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
        SuppressedTerminals = $suppressedTerminalIds.Count
        SuppressedTerminalFormIds = $suppressedTerminalIds -join ';'
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
    HardScriptIssues, ScriptAudit, ScriptSourceCoverage, ScriptProvenance, VariableTelemetry,
    SuppressedInfos, SuppressedPackages, SuppressedScripts, SuppressedTerminals,
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
$distinctSuppressedTerminals = @(
    $results.SuppressedTerminalFormIds -split ';' |
        Where-Object { $_ } |
        Sort-Object -Unique
).Count
Write-Host (
    "Fail-closed telemetry (per-dump unique occurrences): {0:N0} INFO, {1:N0} PACK, {2:N0} SCPT, and {3:N0} TERM." -f
    (Get-NumericPropertySum -Rows $results -Property 'SuppressedInfos'),
    (Get-NumericPropertySum -Rows $results -Property 'SuppressedPackages'),
    (Get-NumericPropertySum -Rows $results -Property 'SuppressedScripts'),
    (Get-NumericPropertySum -Rows $results -Property 'SuppressedTerminals')
)
Write-Host (
    ("Dialogue identity telemetry: {0:N0} exact master anchors, {1:N0} shared-child anchors, " +
     "{2:N0} prototype-distinct, and {3:N0} ambiguous classifications.") -f
    (Get-NumericPropertySum -Rows $results -Property 'DialogueMasterAnchors'),
    (Get-NumericPropertySum -Rows $results -Property 'DialogueSharedChildAnchors'),
    (Get-NumericPropertySum -Rows $results -Property 'DialoguePrototypeDistinct'),
    (Get-NumericPropertySum -Rows $results -Property 'DialogueAmbiguous')
)
Write-Host (
    (("Distinct logged FormIDs across all dumps: {0:N0} INFO, {1:N0} PACK, {2:N0} SCPT, and {3:N0} TERM " +
      "(diagnostic only; allocated IDs may be reused across builds).") -f
        $distinctSuppressedInfos,
        $distinctSuppressedPackages,
        $distinctSuppressedScripts,
        $distinctSuppressedTerminals)
)

if ($failed -ne 0) {
    $results | Where-Object { -not $_.Overall } |
        Format-Table Dump, Failures -AutoSize | Out-Host
    exit 1
}

exit 0
