using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

public sealed class CorpusCertificationScriptContractTests
{
    [Theory]
    [InlineData("Run-DmpCorpus.ps1")]
    [InlineData("Verify-DmpCorpus.ps1")]
    public void SuppressionTelemetryTracksAtomicInlineOwnersAndRetainsStandaloneScripts(string scriptName)
    {
        var source = ReadToolScript(scriptName);

        Assert.Contains("[ValidateSet('INFO', 'PACK', 'SCPT', 'TERM')]", source,
            StringComparison.Ordinal);
        Assert.Contains("'INFO' { @('quest-variable.record-suppressed', 'quest-variable.record-suppressed-no-emitted-producer', 'script-variable.record-suppressed', 'script-variable.owner-not-emitted', 'inline-script.suppress-unsafe-owner') }",
            source, StringComparison.Ordinal);
        Assert.Contains("'PACK' { @('quest-variable.record-suppressed', 'quest-variable.record-suppressed-no-emitted-producer', 'script-variable.record-suppressed', 'inline-script.suppress-unsafe-owner') }",
            source, StringComparison.Ordinal);
        Assert.Contains("'quest-variable.record-suppressed-no-emitted-producer'", source,
            StringComparison.Ordinal);
        Assert.Contains("'TERM' { @('quest-variable.menu-item-suppressed', 'quest-variable.menu-item-suppressed-no-emitted-producer', 'script-variable.menu-item-suppressed', 'inline-script.suppress-unsafe-owner') }", source,
            StringComparison.Ordinal);
        Assert.Contains("'script-variable.owner-not-emitted'", source, StringComparison.Ordinal);
        Assert.Contains("'script.suppress-unsafe-reference-table'", source, StringComparison.Ordinal);
        Assert.Contains("'script.suppress-post-verdict-reference-table'", source, StringComparison.Ordinal);
        Assert.Contains("@('INFO', 'PACK', 'TERM')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerAppendsTerminalColumnsAfterTheExistingManifestSchema()
    {
        var source = ReadToolScript("Run-DmpCorpus.ps1");
        var oldTail = source.IndexOf("Output = $esmPath", StringComparison.Ordinal);
        var terminalCount = source.IndexOf("TermSuppressions = $suppressedTerminalIds.Count",
            StringComparison.Ordinal);
        var terminalIds = source.IndexOf("SuppressedTerminalFormIds = $suppressedTerminalIds -join ';'",
            StringComparison.Ordinal);

        Assert.True(oldTail >= 0 && terminalCount > oldTail && terminalIds > terminalCount);
    }

    [Fact]
    public void VerifierAppendsTerminalColumnsAfterTheExistingCertificationSchema()
    {
        var source = ReadToolScript("Verify-DmpCorpus.ps1");
        var oldTail = source.IndexOf("Failures = $failures -join ';'", StringComparison.Ordinal);
        var terminalCount = source.IndexOf("SuppressedTerminals = $suppressedTerminalIds.Count",
            StringComparison.Ordinal);
        var terminalIds = source.IndexOf("SuppressedTerminalFormIds = $suppressedTerminalIds -join ';'",
            StringComparison.Ordinal);

        Assert.True(oldTail >= 0 && terminalCount > oldTail && terminalIds > terminalCount);
    }

    [Fact]
    public void RunnerAuditsEachDumpAndImportsTheSeparateSourceCoverageReport()
    {
        var source = ReadToolScript("Run-DmpCorpus.ps1");

        Assert.Contains("'dmp', 'scripts', 'audit', $dump.FullName, '--output', $scriptAuditPath",
            source, StringComparison.Ordinal);
        Assert.Contains("$stem.script-audit.csv", source, StringComparison.Ordinal);
        Assert.Contains("script_source_coverage.csv", source, StringComparison.Ordinal);
        Assert.Contains("$scriptAuditHardContradictions -eq 0", source, StringComparison.Ordinal);
        Assert.Contains("$scriptSourceAssessment.Pass", source, StringComparison.Ordinal);
        Assert.Contains("$scriptProvenanceAssessment.Pass", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run-DmpCorpus.ps1")]
    [InlineData("Verify-DmpCorpus.ps1")]
    public void SourceCertificationReconcilesPayloadCountsBytesAndHashes(string scriptName)
    {
        var source = ReadToolScript(scriptName);

        Assert.Contains("Get-PayloadCoverageSummary $SourceRows 'Source' 'SCTX'", source,
            StringComparison.Ordinal);
        Assert.Contains("Get-PayloadCoverageSummary $SubrecordRows 'Subrecord' 'SCTX'", source,
            StringComparison.Ordinal);
        Assert.Contains("Get-PayloadCoverageSummary $SourceRows 'Source' 'SCDA'", source,
            StringComparison.Ordinal);
        Assert.Contains("Test-PayloadHashList", source, StringComparison.Ordinal);
        Assert.Contains("$result.HardContradictions -eq 0", source, StringComparison.Ordinal);
        Assert.Contains("$result.SctxReconciled", source, StringComparison.Ordinal);
        Assert.Contains("$result.ScdaReconciled", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run-DmpCorpus.ps1")]
    [InlineData("Verify-DmpCorpus.ps1")]
    public void SourceCertificationAcceptsAnEmptyScriptPayloadCategory(string scriptName)
    {
        var result = InvokeScriptSourceCoverageAssessment(
            scriptName,
            """
            Get-ScriptSourceCoverageAssessment -SourceRows @() -SubrecordRows @() |
                ConvertTo-Json -Compress
            """);

        Assert.True(result.GetProperty("Pass").GetBoolean());
        Assert.Equal(0, result.GetProperty("HardContradictions").GetInt32());
        Assert.Equal(0, result.GetProperty("SctxCount").GetInt32());
        Assert.True(result.GetProperty("HashIntegrity").GetBoolean());
        Assert.True(result.GetProperty("SctxReconciled").GetBoolean());
        Assert.True(result.GetProperty("ScdaReconciled").GetBoolean());
    }

    [Theory]
    [InlineData("Run-DmpCorpus.ps1")]
    [InlineData("Verify-DmpCorpus.ps1")]
    public void AuthenticSemanticDriftIsDiagnosticOnly(string scriptName)
    {
        var source = ReadToolScript(scriptName);
        var assessmentStart = source.IndexOf("function Get-ScriptSourceCoverageAssessment",
            StringComparison.Ordinal);
        var provenanceStart = source.IndexOf("function Get-ScriptProvenanceAssessment",
            assessmentStart, StringComparison.Ordinal);
        Assert.True(assessmentStart >= 0 && provenanceStart > assessmentStart);

        var assessment = source[assessmentStart..provenanceStart];
        Assert.DoesNotContain("semantic_mismatch_count", assessment, StringComparison.Ordinal);
        Assert.DoesNotContain("semantic_mismatch_categories", assessment, StringComparison.Ordinal);
        Assert.Contains("hard_contradiction", assessment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Run-DmpCorpus.ps1")]
    [InlineData("Verify-DmpCorpus.ps1")]
    public void ProvenanceClaimsRequireExpectedSourceProofAndExactOutputSet(string scriptName)
    {
        var source = ReadToolScript(scriptName);

        Assert.Contains("'script.source-provenance'", source, StringComparison.Ordinal);
        Assert.Contains("'sctx-scda-semantic-match'", source, StringComparison.Ordinal);
        Assert.Contains("'sctx-scda-match-count'", source, StringComparison.Ordinal);
        Assert.Contains("Test-ScriptSemanticProof $metadata", source, StringComparison.Ordinal);
        Assert.Contains("$_.comparison_mismatch_count -eq 0", source, StringComparison.Ordinal);
        Assert.Contains("'expected-sctx-sha256'", source, StringComparison.Ordinal);
        Assert.Contains("'captured-source-utf8-sha256'", source, StringComparison.Ordinal);
        Assert.Contains("'augmentation-declarations-base64'", source, StringComparison.Ordinal);
        Assert.Contains("Test-ScriptSourceProof $metadata", source, StringComparison.Ordinal);
        Assert.Contains("$AuditRows | Where-Object", source, StringComparison.Ordinal);
        Assert.Contains("$_.row_kind -eq 'merged'", source, StringComparison.Ordinal);
        Assert.Contains("$metadata.'sctx-sha256' -ine [string]$coverage.sctx_sha256", source,
            StringComparison.Ordinal);
        Assert.Contains("$eventScdaHash -ieq [string]$coverage.scda_sha256", source,
            StringComparison.Ordinal);
        Assert.Contains("$matched -eq $expectedRows.Count", source,
            StringComparison.Ordinal);
        Assert.Contains("$setEqual", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierAssessmentRejectsMissingProvenanceForEmittedScptSource()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $hash = 'A' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x01000800'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $hash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            Get-ScriptProvenanceAssessment -Events @() -SourceRows $sourceRows -AuditRows @() |
                ConvertTo-Json -Compress
            """);

        Assert.False(result.GetProperty("Pass").GetBoolean());
        Assert.Equal(0, result.GetProperty("EventCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ExpectedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("MatchedCount").GetInt32());
    }

    [Fact]
    public void VerifierAssessmentValidatesCapturedExpectedHashAgainstEmittedPayload()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $actualHash = 'A' * 64
            $expectedHash = 'B' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x01000800'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $actualHash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = '0x00123456'; 'emitted-form-id' = '0x01000800'
                'editor-id' = 'CapturedScript'; 'source-origin' = 'runtime-same-object'
                'sctx-proof-kind' = 'captured-exact'; 'sctx-sha256' = $actualHash
                'expected-sctx-sha256' = $expectedHash; 'base-sctx-sha256' = $expectedHash
                'captured-source-utf8-sha256' = $expectedHash
                'augmentation-declaration-count' = '0'; 'augmentation-declarations-base64' = $null
                'augmentation-declarations-sha256' = $null; 'sctx-decoded-length' = '1'
                'scda-sha256' = $null; 'scda-length' = '0'
                'bytecode-changed-from-source' = 'false'; 'tables-changed-from-source' = 'false'
                'sctx-scda-semantic-match' = 'source-only-no-scda'
                'sctx-scda-match-count' = $null; 'sctx-scda-tolerated-count' = $null
                'sctx-scda-tolerated-categories' = $null
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x01000800'; Metadata = $metadata
            }
            $auditRows = @(
                [pscustomobject]@{ row_kind = 'merged'; form_id = '0x00123456'; source_sha256 = $expectedHash }
                [pscustomobject]@{ row_kind = 'runtime'; form_id = '0x00123456'; source_sha256 = $expectedHash }
            )
            Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows $auditRows |
                ConvertTo-Json -Compress
            """);

        Assert.False(result.GetProperty("Pass").GetBoolean());
        Assert.Equal(1, result.GetProperty("EventCount").GetInt32());
        Assert.Equal(1, result.GetProperty("ExpectedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("MatchedCount").GetInt32());
    }

    [Fact]
    public void VerifierAssessmentRejectsDirectSourceAbsentFromSameDumpAudit()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $sourceHash = 'A' * 64
            $unrelatedHash = 'B' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x01000800'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $sourceHash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = '0x00123456'; 'emitted-form-id' = '0x01000800'
                'editor-id' = 'CapturedScript'; 'source-origin' = 'dmp-fragment'
                'sctx-proof-kind' = 'captured-exact'; 'sctx-sha256' = $sourceHash
                'expected-sctx-sha256' = $sourceHash; 'base-sctx-sha256' = $sourceHash
                'captured-source-utf8-sha256' = $sourceHash
                'augmentation-declaration-count' = '0'; 'augmentation-declarations-base64' = $null
                'augmentation-declarations-sha256' = $null; 'sctx-decoded-length' = '1'
                'scda-sha256' = $null; 'scda-length' = '0'
                'bytecode-changed-from-source' = 'false'; 'tables-changed-from-source' = 'false'
                'sctx-scda-semantic-match' = 'source-only-no-scda'
                'sctx-scda-match-count' = $null; 'sctx-scda-tolerated-count' = $null
                'sctx-scda-tolerated-categories' = $null
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x01000800'; Metadata = $metadata
            }
            $auditRows = @([pscustomobject]@{
                row_kind = 'merged'; form_id = '0x00123456'; source_sha256 = $unrelatedHash
            })
            Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows $auditRows |
                ConvertTo-Json -Compress
            """);

        Assert.False(result.GetProperty("Pass").GetBoolean());
        Assert.Equal(1, result.GetProperty("ExpectedCount").GetInt32());
        Assert.Equal(0, result.GetProperty("MatchedCount").GetInt32());
    }

    [Fact]
    public void VerifierAssessmentAcceptsStructurallyEmptySourceOnlyPreservation()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $sourceHash = 'A' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x01000800'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $sourceHash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = '0x00123456'; 'emitted-form-id' = '0x01000800'
                'editor-id' = 'SourceOnlyScript'; 'source-origin' = 'dmp-fragment'
                'sctx-proof-kind' = 'captured-exact'; 'sctx-sha256' = $sourceHash
                'expected-sctx-sha256' = $sourceHash; 'base-sctx-sha256' = $sourceHash
                'captured-source-utf8-sha256' = $sourceHash
                'augmentation-declaration-count' = '0'; 'augmentation-declarations-base64' = $null
                'augmentation-declarations-sha256' = $null; 'sctx-decoded-length' = '1'
                'scda-sha256' = $null; 'scda-length' = '0'
                'bytecode-changed-from-source' = 'false'; 'tables-changed-from-source' = 'false'
                'sctx-scda-semantic-match' = 'source-only-no-scda'
                'sctx-scda-match-count' = $null; 'sctx-scda-tolerated-count' = $null
                'sctx-scda-tolerated-categories' = $null
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x01000800'; Metadata = $metadata
            }
            $auditRows = @([pscustomobject]@{
                row_kind = 'merged'; form_id = '0x00123456'; source_sha256 = $sourceHash
                content_classification = 'source-only'; scda_length = '0'
                effective_variable_count = '0'; variable_list_count = '0'; reference_list_count = '0'
                structural_diagnostics = $null; hard_contradictions = $null
            })
            Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows $auditRows |
                ConvertTo-Json -Compress
            """);

        Assert.True(result.GetProperty("Pass").GetBoolean());
        Assert.Equal(1, result.GetProperty("MatchedCount").GetInt32());
    }

    [Fact]
    public void VerifierAssessmentRequiresStructurallyExactZeroMismatchCompiledSourceAudit()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $sourceHash = 'A' * 64
            $scdaHash = 'B' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x01000800'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $sourceHash; sctx_decoded_length = '1'
                scda_count = '1'; scda_sha256 = $scdaHash; scda_length = '4'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = '0x00123456'; 'emitted-form-id' = '0x01000800'
                'editor-id' = 'CapturedScript'; 'source-origin' = 'runtime-same-object'
                'sctx-proof-kind' = 'captured-exact'; 'sctx-sha256' = $sourceHash
                'expected-sctx-sha256' = $sourceHash; 'base-sctx-sha256' = $sourceHash
                'captured-source-utf8-sha256' = $sourceHash
                'augmentation-declaration-count' = '0'; 'augmentation-declarations-base64' = $null
                'augmentation-declarations-sha256' = $null; 'sctx-decoded-length' = '1'
                'scda-sha256' = $scdaHash; 'scda-length' = '4'
                'bytecode-changed-from-source' = 'true'; 'tables-changed-from-source' = 'false'
                'sctx-scda-semantic-match' = 'proven-zero-nontolerated-mismatches'
                'sctx-scda-match-count' = '3'; 'sctx-scda-tolerated-count' = '1'
                'sctx-scda-tolerated-categories' = 'NumberFormat=1'
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x01000800'; Metadata = $metadata
            }
            $merged = [pscustomobject]@{
                row_kind = 'merged'; form_id = '0x00123456'; source_sha256 = $sourceHash
                content_classification = 'both'; scda_length = '4'
                declaration_identity_verdict = 'exact'; declared_scda_length_matches = 'true'
                header_variable_count_matches_list = 'true'
                effective_variable_count_matches_list = 'true'
                header_reference_count_matches_list = 'true'
                structural_diagnostics = $null; hard_contradictions = $null
                comparison_mismatch_count = '0'; comparison_match_count = '3'
                comparison_tolerated_count = '1'; comparison_tolerated_categories = 'NumberFormat=1'
            }
            $runtime = [pscustomobject]@{
                row_kind = 'runtime'; form_id = '0x00123456'; source_sha256 = $sourceHash
            }
            $valid = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @($merged, $runtime)).Pass
            $merged.comparison_mismatch_count = '1'
            $mismatch = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @($merged, $runtime)).Pass
            $merged.comparison_mismatch_count = '0'
            $merged.declaration_identity_verdict = 'mismatch'
            $declarationMismatch = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @($merged, $runtime)).Pass
            [pscustomobject]@{
                Valid = $valid
                Mismatch = $mismatch
                DeclarationMismatch = $declarationMismatch
            } | ConvertTo-Json -Compress
            """);

        Assert.True(result.GetProperty("Valid").GetBoolean());
        Assert.False(result.GetProperty("Mismatch").GetBoolean());
        Assert.False(result.GetProperty("DeclarationMismatch").GetBoolean());
    }

    [Fact]
    public void VerifierAssessmentAcceptsFreshLocalAugmentationProofAndRejectsCorruption()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $outputHash = 'A' * 64
            $baseHash = 'B' * 64
            $declarationBytes = [Text.Encoding]::ASCII.GetBytes('short RecoveredFlag')
            $declarationHash = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($declarationBytes))
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x0000ABCD'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $outputHash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = $null; 'emitted-form-id' = '0x0000ABCD'
                'editor-id' = 'RetailScript'; 'source-origin' = 'augmentation'
                'sctx-proof-kind' = 'master-plus-declarations'; 'sctx-sha256' = $outputHash
                'expected-sctx-sha256' = $outputHash; 'base-sctx-sha256' = $baseHash
                'captured-source-utf8-sha256' = $null
                'augmentation-declaration-count' = '1'
                'augmentation-declarations-base64' = [Convert]::ToBase64String($declarationBytes)
                'augmentation-declarations-sha256' = $declarationHash; 'sctx-decoded-length' = '1'
                'scda-sha256' = $null; 'scda-length' = '0'
                'bytecode-changed-from-source' = 'false'; 'tables-changed-from-source' = 'true'
                'sctx-scda-semantic-match' = 'master-base-plus-fresh-local-declarations'
                'sctx-scda-match-count' = $null; 'sctx-scda-tolerated-count' = $null
                'sctx-scda-tolerated-categories' = $null
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x0000ABCD'; Metadata = $metadata
            }
            $valid = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @()).Pass
            $metadata.'augmentation-declarations-sha256' = 'C' * 64
            $corrupt = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @()).Pass
            [pscustomobject]@{ Valid = $valid; Corrupt = $corrupt } | ConvertTo-Json -Compress
            """);

        Assert.True(result.GetProperty("Valid").GetBoolean());
        Assert.False(result.GetProperty("Corrupt").GetBoolean());
    }

    [Fact]
    public void VerifierAssessmentAcceptsPreservedMasterSourceForStorageOnlyAugmentation()
    {
        var result = InvokeVerifierProvenanceAssessment(
            """
            $sourceHash = 'A' * 64
            $sourceRows = @([pscustomobject]@{
                record_type = 'SCPT'; form_id = '0x0000ABCD'; sctx_present = 'True'
                sctx_count = '1'; sctx_sha256 = $sourceHash; sctx_decoded_length = '1'
                scda_count = '0'; scda_sha256 = $null; scda_length = '0'
            })
            $metadata = [pscustomobject]@{
                'source-form-id' = $null; 'emitted-form-id' = '0x0000ABCD'
                'editor-id' = 'RetailScript'; 'source-origin' = 'augmentation'
                'sctx-proof-kind' = 'master-executable-source'; 'sctx-sha256' = $sourceHash
                'expected-sctx-sha256' = $sourceHash; 'base-sctx-sha256' = $sourceHash
                'captured-source-utf8-sha256' = $null
                'augmentation-declaration-count' = '0'
                'augmentation-declarations-base64' = $null
                'augmentation-declarations-sha256' = $null; 'sctx-decoded-length' = '1'
                'scda-sha256' = $null; 'scda-length' = '0'
                'bytecode-changed-from-source' = 'false'; 'tables-changed-from-source' = 'true'
                'sctx-scda-semantic-match' = 'master-executable-source-augmented-local-table-incomplete'
                'sctx-scda-match-count' = $null; 'sctx-scda-tolerated-count' = $null
                'sctx-scda-tolerated-categories' = $null
            }
            $event = [pscustomobject]@{
                Kind = 'Event'; Code = 'script.source-provenance'; FormType = 'SCPT'
                FormId = '0x0000ABCD'; Metadata = $metadata
            }
            $valid = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @()).Pass
            $metadata.'expected-sctx-sha256' = 'B' * 64
            $corrupt = (Get-ScriptProvenanceAssessment `
                -Events @($event) -SourceRows $sourceRows -AuditRows @()).Pass
            [pscustomobject]@{ Valid = $valid; Corrupt = $corrupt } | ConvertTo-Json -Compress
            """);

        Assert.True(result.GetProperty("Valid").GetBoolean());
        Assert.False(result.GetProperty("Corrupt").GetBoolean());
    }

    private static string ReadToolScript(string name) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "tools", name));

    private static JsonElement InvokeVerifierProvenanceAssessment(string fixture)
    {
        var verifierPath = Path.Combine(FindRepositoryRoot(), "tools", "Verify-DmpCorpus.ps1");
        var escapedVerifierPath = verifierPath.Replace("'", "''", StringComparison.Ordinal);
        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                '{{escapedVerifierPath}}', [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
            foreach ($name in @(
                'Test-ObjectProperties',
                'Test-ScriptSourceProof',
                'Test-ScriptSemanticProof',
                'Get-ScriptProvenanceAssessment')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Missing function $name" }
                Invoke-Expression $function.Extent.Text
            }
            {{fixture}}
            """;

        var harnessPath = Path.Combine(Path.GetTempPath(), $"corpus-provenance-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(harnessPath, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    harnessPath,
                },
            });
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "PowerShell provenance harness timed out.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(error), error);
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    private static JsonElement InvokeScriptSourceCoverageAssessment(string scriptName, string fixture)
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "tools", scriptName);
        var escapedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null
            $parseErrors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                '{{escapedScriptPath}}', [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
            foreach ($name in @(
                'Get-NumericPropertySum',
                'Get-PayloadCoverageSummary',
                'Test-PayloadHashList',
                'Get-ScriptSourceCoverageAssessment')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Missing function $name" }
                Invoke-Expression $function.Extent.Text
            }
            {{fixture}}
            """;

        var harnessPath = Path.Combine(Path.GetTempPath(), $"corpus-source-coverage-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(harnessPath, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    harnessPath,
                },
            });
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "PowerShell source-coverage harness timed out.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(error), error);
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
