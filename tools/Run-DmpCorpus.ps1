<#
.SYNOPSIS
Builds and validates the complete DMP-to-ESM corpus sequentially.

.DESCRIPTION
Converts every selected .dmp under Sample/MemoryDump with converter validation
enabled and the July then April dialogue CSVs. Each
successful output is subsequently deep-validated, parsed for dialogue stats,
and analyzed for ESM coverage. Every input also receives a same-dump script
source/bytecode audit, while emitted SCTX/SCDA payload hashes are reconciled
with structured provenance events. Structural dialogue-identity telemetry is
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
        'script_source_coverage.csv' =
            'record_type,form_id,block_index,block,scda_present,scda_count,scda_length,scda_sha256,sctx_present,sctx_count,sctx_raw_length,sctx_decoded_length,sctx_sha256,sctx_nul_termination,source_classification,semantic_comparison_available,semantic_statement_count,semantic_match_count,semantic_mismatch_count,semantic_tolerated_count,semantic_mismatch_categories,semantic_tolerated_categories,source_declaration_count,slsd_variable_count,declaration_slsd_identity_verdict,hard_contradiction,hard_contradiction_reason'
    }

    foreach ($entry in $headers.GetEnumerator()) {
        $path = Join-Path $Directory $entry.Key
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Utf8Text $path ($entry.Value + [Environment]::NewLine)
        }
    }
}

function Ensure-ScriptAuditReport {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Utf8Text $Path (
            'row_kind,form_id,copy_ordinal,dump_offset,editor_id,runtime_copy_count,runtime_copy_status,content_classification,source_char_length,source_utf8_length,source_sha256,source_terminated_proof,scda_length,scda_sha256,declared_scda_length,declared_scda_length_matches,header_variable_count,effective_variable_count,variable_list_count,variable_metadata_complete,variables_complete,header_variable_count_matches_list,effective_variable_count_matches_list,header_reference_count,reference_list_count,references_complete,header_reference_count_matches_list,declaration_identity_verdict,declaration_count,declaration_identities,slsd_identities,declaration_identity_details,source_statement_count,decompiled_statement_count,comparison_status,comparison_match_count,comparison_mismatch_count,comparison_tolerated_count,comparison_mismatch_categories,comparison_tolerated_categories,comparison_match_rate,proven_trivial_scda,structural_diagnostics,hard_contradictions' +
            [Environment]::NewLine)
    }
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
            -not (Test-JsonObjectProperties $metadata $requiredMetadata)) {
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

function Test-SuppressionEvents {
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
    $scriptAuditLog = Join-Path $outputPath "$stem.script-audit.log"
    $scriptAuditPath = Join-Path $outputPath "$stem.script-audit.csv"
    $eventLog = Join-Path $outputPath "$stem.events.jsonl"
    $coverageDirectory = Join-Path $outputPath "$stem.coverage"

    Write-Host "[$ordinal/$($dumps.Count)] $($dump.Name)"

    $convertArguments = @(
        'dmp', 'to-esm', $dump.FullName,
        '--pc-esm', $MasterEsm,
        '--output', $esmPath,
        '--validate',
        '--cell-authority', $CellAuthority,
        '--event-log-jsonl', $eventLog,
        '--dialogue-audio-csv', $JulyDialogueCsv,
        '--dialogue-audio-csv', $AprilDialogueCsv
    )
    $conversion = Invoke-DotnetDll $DotnetPath $BethesdaMultitoolDll $convertArguments $buildLog
    $scriptAudit = Invoke-DotnetDll $DotnetPath $EsmAnalyzerDll @(
        'dmp', 'scripts', 'audit', $dump.FullName, '--output', $scriptAuditPath
    ) $scriptAuditLog

    if (Test-Path -LiteralPath $esmPath -PathType Leaf) {
        $deep = Invoke-DotnetDll $DotnetPath $EsmAnalyzerDll @(
            'validate', 'deep', $esmPath
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
    Ensure-ScriptAuditReport $scriptAuditPath
    $recordCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'record_coverage.csv'))
    $subrecordCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'subrecord_coverage.csv'))
    $scriptCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'script_bytecode_coverage.csv'))
    $scriptSourceCoverage = @(Import-Csv -LiteralPath (Join-Path $coverageDirectory 'script_source_coverage.csv'))
    $scriptAuditRows = @(Import-Csv -LiteralPath $scriptAuditPath)
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
    $unparsedCount = Get-NumericPropertySum -Rows $unparsedRows -Property 'count'
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

    $scriptAuditHardContradictions = @(
        $scriptAuditRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.hard_contradictions) }
    ).Count
    $scriptAuditIntegrity = @($scriptAuditRows | Where-Object {
        $_.row_kind -notin @('runtime', 'merged') -or
        $_.form_id -notmatch '^0x[0-9A-Fa-f]{8}$' -or
        ([int64]$_.source_utf8_length -gt 0 -and $_.source_sha256 -notmatch '^[0-9A-Fa-f]{64}$') -or
        ([int64]$_.scda_length -gt 0 -and $_.scda_sha256 -notmatch '^[0-9A-Fa-f]{64}$')
    }).Count -eq 0
    $scriptAuditPass =
        $scriptAudit.ExitCode -eq 0 -and
        $scriptAuditHardContradictions -eq 0 -and
        $scriptAuditIntegrity

    $scriptSourceAssessment = Get-ScriptSourceCoverageAssessment `
        $scriptSourceCoverage $subrecordCoverage
    $scriptProvenanceAssessment = Get-ScriptProvenanceAssessment `
        $conversionEvents $scriptSourceCoverage $scriptAuditRows

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
    $scdaCount = Get-NumericPropertySum -Rows $scdaCountRows -Property 'count'
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
        $scriptAuditPass -and
        $scriptSourceAssessment.Pass -and
        $scriptProvenanceAssessment.Pass -and
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
    $suppressedTerminalIds = @(Get-SuppressedRecordIdsFromEvents $conversionEvents 'TERM')
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
        TermSuppressions = $suppressedTerminalIds.Count
        SuppressedTerminalFormIds = $suppressedTerminalIds -join ';'
        ScriptAuditExit = $scriptAudit.ExitCode
        ScriptAuditPass = $scriptAuditPass
        ScriptAuditRows = $scriptAuditRows.Count
        ScriptAuditHardContradictions = $scriptAuditHardContradictions
        ScriptAuditSha256 = Get-Sha256 $scriptAuditPath
        ScriptSourceCoverageRows = $scriptSourceCoverage.Count
        ScriptSourceHardContradictions = $scriptSourceAssessment.HardContradictions
        ScriptSourceSctxBlocks = $scriptSourceAssessment.SctxCount
        ScriptSourceSctxReconciled = $scriptSourceAssessment.SctxReconciled
        ScriptSourceScdaReconciled = $scriptSourceAssessment.ScdaReconciled
        ScriptSourceHashIntegrity = $scriptSourceAssessment.HashIntegrity
        ScriptSourceCoveragePass = $scriptSourceAssessment.Pass
        ScriptSourceCoverageSha256 = Get-Sha256 (
            Join-Path $coverageDirectory 'script_source_coverage.csv')
        ScriptProvenanceEvents = $scriptProvenanceAssessment.EventCount
        ScriptProvenanceMatched = $scriptProvenanceAssessment.MatchedCount
        ScriptProvenancePass = $scriptProvenanceAssessment.Pass
    }
    $results.Add($row)
    $results | Export-Csv -LiteralPath $manifestPath -NoTypeInformation

    Write-Host (
        "  exits convert/audit/deep/dialogue/coverage={0}/{1}/{2}/{3}/{4}; success={5}; {6:N1}s" -f
        $conversion.ExitCode,
        $scriptAudit.ExitCode,
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
