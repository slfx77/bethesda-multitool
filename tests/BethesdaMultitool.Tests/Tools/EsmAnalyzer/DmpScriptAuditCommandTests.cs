using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using EsmAnalyzer.Commands;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

public sealed class DmpScriptAuditCommandTests
{
    [Fact]
    public void Build_CoversEveryRawAndMergedCopyInDeterministicOrder()
    {
        var source = "scn AuditScript\nshort Flag";
        var first = CompleteRuntime(0x01000002, 0x20, source, ScriptNameOnlyBigEndian(),
            [new ScriptVariableInfo(1, "Flag", 1)]);
        var second = first with { DumpOffset = 0x10 };
        var collection = new RecordCollection
        {
            RuntimeScripts = [first, second],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000002,
                    EditorId = "Audit,Script",
                    SourceText = source,
                    SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
                    CompiledData = ScriptNameOnlyBigEndian(),
                    CompiledSize = 4,
                    VariableCount = 1,
                    LastVariableId = 1,
                    Variables = [new ScriptVariableInfo(1, "Flag", 1)],
                    IsBigEndian = true,
                    Offset = 0x30
                }
            ]
        };

        var report = DmpScriptAuditAnalyzer.Build(collection);

        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(["runtime", "runtime", "merged"], report.Rows.Select(static row => row.RowKind));
        Assert.Equal([0x10L, 0x20L, 0x30L], report.Rows.Select(static row => row.DumpOffset));
        Assert.All(report.Rows, row => Assert.Equal("equivalent", row.RuntimeCopyStatus));
        Assert.All(report.Rows, row => Assert.Equal("both", row.ContentClassification));
        Assert.All(report.Rows, row => Assert.Equal("exact", row.DeclarationIdentityVerdict));
        Assert.Equal("same-dump-runtime-nul-proven", report.Rows[2].SourceTerminatedProof);
        Assert.Equal(0, report.HardContradictionCount);

        var csv = DmpScriptAuditCsv.Serialize(report.Rows[2]);
        Assert.Contains("\"Audit,Script\"", csv, StringComparison.Ordinal);
        Assert.Contains(report.Rows[2].SourceSha256!, csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ConflictingRuntimeCopiesAreAHardContradiction()
    {
        var first = CompleteRuntime(0x01000003, 0x10, "scn A", ScriptNameOnlyBigEndian(), []);
        var second = first with { DumpOffset = 0x20, EditorId = "DifferentIdentity" };
        var collection = new RecordCollection { RuntimeScripts = [first, second] };

        var report = DmpScriptAuditAnalyzer.Build(collection);

        Assert.Equal(1, report.HardContradictionCount);
        Assert.All(report.Rows, row =>
        {
            Assert.Equal("conflicting", row.RuntimeCopyStatus);
            Assert.Contains("runtime-copy-conflict", row.HardContradictions, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Build_ConflictingRuntimeCopyMatchingMergedTextDoesNotClaimSameObjectProof()
    {
        var first = CompleteRuntime(0x0100000A, 0x10, "scn First", ScriptNameOnlyBigEndian(), []);
        var second = first with { DumpOffset = 0x20, SourceText = "scn Second" };
        var merged = new ScriptRecord
        {
            FormId = first.FormId,
            SourceText = second.SourceText,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            CompiledData = ScriptNameOnlyBigEndian(),
            CompiledSize = 4,
            IsBigEndian = true,
            Offset = 0x30
        };

        var report = DmpScriptAuditAnalyzer.Build(new RecordCollection
        {
            RuntimeScripts = [first, second],
            Scripts = [merged]
        });

        var mergedRow = Assert.Single(report.Rows, static row => row.RowKind == "merged");
        Assert.Equal("conflicting", mergedRow.RuntimeCopyStatus);
        Assert.Equal("fragment-termination-unrepresented", mergedRow.SourceTerminatedProof);
    }

    [Fact]
    public void Build_MatchingRuntimeTextWithoutSameObjectOriginDoesNotClaimSameObjectProof()
    {
        var runtime = CompleteRuntime(0x0100000B, 0x10, "scn Fragment", ScriptNameOnlyBigEndian(), []);
        var merged = new ScriptRecord
        {
            FormId = runtime.FormId,
            SourceText = runtime.SourceText,
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            CompiledData = ScriptNameOnlyBigEndian(),
            CompiledSize = 4,
            IsBigEndian = true,
            Offset = 0x20
        };

        var report = DmpScriptAuditAnalyzer.Build(new RecordCollection
        {
            RuntimeScripts = [runtime],
            Scripts = [merged]
        });

        var mergedRow = Assert.Single(report.Rows, static row => row.RowKind == "merged");
        Assert.Equal("fragment-termination-unrepresented", mergedRow.SourceTerminatedProof);
    }

    [Fact]
    public void Build_ExecutableSourceWithProvenScriptNameOnlyScdaIsHard()
    {
        var script = CompleteRuntime(
            0x01000004,
            0x10,
            "scn Stubbed\nBegin GameMode\nEnd",
            ScriptNameOnlyBigEndian(),
            []);

        var row = Assert.Single(DmpScriptAuditAnalyzer.Build(
            new RecordCollection { RuntimeScripts = [script] }).Rows);

        Assert.True(row.ProvenTrivialScda);
        Assert.Equal(2, row.SourceStatementCount);
        Assert.Contains("executable-source-with-trivial-scda", row.HardContradictions, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ScriptNamePrefixOfIncompleteScdaIsNotCalledAProvenStub()
    {
        var script = CompleteRuntime(
            0x01000009,
            0x10,
            "scn Partial\nBegin GameMode\nEnd",
            ScriptNameOnlyBigEndian(),
            []) with
        {
            DataSize = 20
        };

        var row = Assert.Single(DmpScriptAuditAnalyzer.Build(
            new RecordCollection { RuntimeScripts = [script] }).Rows);

        Assert.False(row.ProvenTrivialScda);
        Assert.DoesNotContain("executable-source-with-trivial-scda", row.HardContradictions, StringComparison.Ordinal);
        Assert.Contains("declared-scda-length-mismatch", row.StructuralDiagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CompleteFlagContradictingEffectiveVariableCountIsHard()
    {
        var script = CompleteRuntime(
            0x01000005,
            0x10,
            "scn Counts\nshort Flag",
            ScriptNameOnlyBigEndian(),
            [new ScriptVariableInfo(1, "Flag", 1)]) with
        {
            VariableCount = 2
        };

        var row = Assert.Single(DmpScriptAuditAnalyzer.Build(
            new RecordCollection { RuntimeScripts = [script] }).Rows);

        Assert.Contains("variables-complete-effective-count-mismatch", row.HardContradictions, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OrdinarySemanticMismatchRemainsDiagnosticOnly()
    {
        byte[] scda = [0x00, 0x1D, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00];
        var script = CompleteRuntime(
            0x01000006,
            0x10,
            "scn Mismatch\nBegin GameMode\nEnd",
            scda,
            []);

        var report = DmpScriptAuditAnalyzer.Build(new RecordCollection { RuntimeScripts = [script] });
        var row = Assert.Single(report.Rows);

        Assert.Equal("compared", row.ComparisonStatus);
        Assert.True(row.ComparisonMismatchCount > 0);
        Assert.Equal(string.Empty, row.HardContradictions);
        Assert.Equal(0, report.HardContradictionCount);
    }

    [Fact]
    public void DeclarationAuditRequiresExactNameAndStorageIdentity()
    {
        const string source = "scn Identity\nshort RetailFlag\nref Marker";
        var exact = ScriptDeclarationIdentityAudit.Compare(
            source,
            [new ScriptVariableInfo(1, "retailflag", 1), new ScriptVariableInfo(2, "Marker", 0)]);
        var mismatch = ScriptDeclarationIdentityAudit.Compare(
            source,
            [new ScriptVariableInfo(1, "RetailFlagSimilar", 1), new ScriptVariableInfo(2, "Marker", 1)]);

        Assert.Equal("exact", exact.Verdict);
        Assert.Equal("mismatch", mismatch.Verdict);
        Assert.Contains("missing-slsd:RetailFlag", mismatch.Details, StringComparison.Ordinal);
        Assert.Contains("storage-mismatch:Marker", mismatch.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationAuditRecognizesExactBlockLocalReferences()
    {
        const string source = """
                              scn BlockLocals
                              short state
                              begin GameMode
                                  ref refvar
                                  ref target
                              end
                              """;

        var result = ScriptDeclarationIdentityAudit.Compare(
            source,
            [
                new ScriptVariableInfo(1, "state", 1),
                new ScriptVariableInfo(2, "refvar", 0),
                new ScriptVariableInfo(3, "target", 0)
            ]);

        Assert.Equal("exact", result.Verdict);
        Assert.Equal(3, result.DeclarationCount);
    }

    [Fact]
    public void CsvWrite_IsDeterministicAcrossInputOrderAndHasNoBom()
    {
        var early = CompleteRuntime(0x01000007, 0x10, "scn Early", ScriptNameOnlyBigEndian(), []);
        var late = CompleteRuntime(0x01000008, 0x20, "scn Late", ScriptNameOnlyBigEndian(), []);
        var forward = DmpScriptAuditAnalyzer.Build(new RecordCollection { RuntimeScripts = [early, late] });
        var reverse = DmpScriptAuditAnalyzer.Build(new RecordCollection { RuntimeScripts = [late, early] });
        var directory = Path.Combine(Path.GetTempPath(), $"dmp-script-audit-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(directory, "first.csv");
        var secondPath = Path.Combine(directory, "second.csv");
        try
        {
            DmpScriptAuditCsv.Write(firstPath, forward.Rows);
            DmpScriptAuditCsv.Write(secondPath, reverse.Rows);

            var first = File.ReadAllBytes(firstPath);
            var second = File.ReadAllBytes(secondPath);
            Assert.Equal(first, second);
            Assert.False(first.Length >= 3 && first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF);
            Assert.StartsWith("row_kind,form_id,copy_ordinal", File.ReadAllText(firstPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static RuntimeScriptData CompleteRuntime(
        uint formId,
        long offset,
        string source,
        byte[] scda,
        List<ScriptVariableInfo> variables) => new()
    {
        FormId = formId,
        EditorId = $"Script{formId:X8}",
        HeaderVariableCount = (uint)variables.Count,
        VariableCount = (uint)variables.Count,
        RefObjectCount = 0,
        DataSize = (uint)scda.Length,
        LastVariableId = variables.Count == 0 ? 0 : variables.Max(static variable => variable.Index),
        IsCompiled = true,
        SourceText = source,
        CompiledData = scda,
        Variables = variables,
        ReferencedObjects = [],
        VariableMetadataComplete = true,
        VariablesComplete = true,
        ReferencedObjectsComplete = true,
        DumpOffset = offset
    };

    private static byte[] ScriptNameOnlyBigEndian() => [0x00, 0x1D, 0x00, 0x00];
}
