using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

[Collection("Logger")]
public sealed class CapturedScriptSourceCorrespondenceTests : IDisposable
{
    private readonly StringWriter _output = new();

    public CapturedScriptSourceCorrespondenceTests()
    {
        Logger.Instance.Reset();
        Logger.SetOutput(_output);
    }

    [Fact]
    public void Enforce_ExactCompiledSourcePairPreservesSctx()
    {
        const string text = "scn MatchingScript\nBegin GameMode\nSet recovered to 1\nEnd";
        var script = CompiledSource(text, text);

        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(script);

        Assert.Equal(text, result.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.RuntimeSameObject, result.SourceTextOrigin);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Accepted,
            result.SourceTextCorrespondenceStatus);
        Assert.DoesNotContain("rejected captured SCTX", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_DebugBCHostageEqualStatementCountButDifferentRefRejectsStaleSctx()
    {
        const string source =
            "scn DebugBCHostageScript\nBegin GameMode\n"
            + "if BCHostageREF.GetDead\nSet BCHostageState to 1\nendif\nEnd";
        const string decompiled =
            "ScriptName DebugBCHostageScript\nBegin GameMode\n"
            + "if DebugBCTrooperREF.GetDead\nSet BCHostageState to 1\nendif\nEnd";
        var script = CompiledSource(source, decompiled);

        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(script);

        Assert.Null(result.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.None, result.SourceTextOrigin);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Rejected,
            result.SourceTextCorrespondenceStatus);
        var diagnostic = _output.ToString();
        Assert.Contains("SCPT 0x00123456", diagnostic, StringComparison.Ordinal);
        Assert.Contains("non-tolerated-mismatches=1 [Other=1]", diagnostic, StringComparison.Ordinal);
        Assert.Contains("source-origin=RuntimeSameObject", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "scn Mismatch\nBegin GameMode\nif (GetDead Player)\nendif\nEnd",
        "ScriptName Mismatch\nBegin GameMode\nif GetDead Player\nendif\nEnd",
        "Parenthesization")]
    [InlineData(
        "scn Mismatch\nBegin GameMode\nSet questRef.actualVar to 1\nEnd",
        "ScriptName Mismatch\nBegin GameMode\nSet questRef.var0 to 1\nEnd",
        "UnresolvedVariable")]
    public void Enforce_NonToleratedComparerCategoryRejectsSctx(
        string source,
        string decompiled,
        string expectedCategory)
    {
        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(
            CompiledSource(source, decompiled));

        Assert.Null(result.SourceText);
        Assert.Contains($"[{expectedCategory}=1]", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_ToleratedNumberFormattingDifferencePreservesSctx()
    {
        const string source = "scn Tolerated\nBegin GameMode\nSet recovered to 1\nEnd";
        const string decompiled = "ScriptName Tolerated\nBegin GameMode\nSet recovered to 1.0\nEnd";
        var script = CompiledSource(source, decompiled);

        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(script);

        Assert.Equal(source, result.SourceText);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Accepted,
            result.SourceTextCorrespondenceStatus);
    }

    [Fact]
    public void Enforce_SourceOnlyRecoveryHasNoScdaGate()
    {
        var script = new ScriptRecord
        {
            FormId = 0x00123457,
            SourceText = "scn SourceOnlyRecovery\nshort recovered",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
        };

        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(script);

        Assert.Equal(script.SourceText, result.SourceText);
        Assert.Equal(ScriptSourceCorrespondenceStatus.AcceptedSourceOnly,
            result.SourceTextCorrespondenceStatus);
    }

    [Fact]
    public void Enforce_MalformedScdaRejectsHeaderOnlySctxDespiteZeroComparableStatements()
    {
        var script = new ScriptRecord
        {
            FormId = 0x00123458,
            EditorId = "MalformedHeaderOnly",
            CompiledData = [0xFF],
            IsBigEndian = true,
            IsCompiled = true,
            SourceText = "scn MalformedHeaderOnly",
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            DecompiledText = "; Unknown opcode 0xFF",
        };

        var result = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(script);

        Assert.Null(result.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.None, result.SourceTextOrigin);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Rejected,
            result.SourceTextCorrespondenceStatus);
        var diagnostic = _output.ToString();
        Assert.Contains("rejection-categories=[UnsafeBytecode=1]", diagnostic,
            StringComparison.Ordinal);
        Assert.Contains("bytecode-diagnostic-count=", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneContract_AllowsCaseInsensitiveIdentityWithExactLocalStorage()
    {
        const string text = "scn capturedscript\nfloat localstate";
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(text, text) with
            {
                EditorId = "CapturedScript",
                VariableCount = 1,
                Variables = [new ScriptVariableInfo(1, "LocalState", 0)],
            });

        Assert.Null(decision.SourceIssue);
        Assert.Equal(text, decision.Script.SourceText);
        Assert.False(decision.Script.IsIncompleteExecutableBundle);
    }

    [Fact]
    public void StandaloneContract_DropsSourceWithDifferentScriptIdentity()
    {
        const string text = "scn DifferentScript\nBegin GameMode\nEnd";
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(text, text));

        Assert.Null(decision.Script.SourceText);
        Assert.Contains("does not exactly match EDID", decision.SourceIssue, StringComparison.Ordinal);
        Assert.False(decision.Script.IsIncompleteExecutableBundle);
    }

    [Fact]
    public void StandaloneContract_DropsSourceWhenLocalStorageDoesNotMatchSlsd()
    {
        const string text = "scn CapturedScript\nshort LocalState";
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(text, text) with
            {
                VariableCount = 1,
                Variables = [new ScriptVariableInfo(1, "localstate", 0)],
            });

        Assert.Null(decision.Script.SourceText);
        Assert.Contains("storage does not match", decision.SourceIssue, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneContract_ReferenceDeclarationUsesFloatOrReferenceSlsdStorage()
    {
        const string text = "scn CapturedScript\nref TargetRef";
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(text, text) with
            {
                VariableCount = 1,
                Variables = [new ScriptVariableInfo(1, "targetref", 0)],
            });

        Assert.Null(decision.SourceIssue);
        Assert.Equal(text, decision.Script.SourceText);
    }

    [Fact]
    public void InlineContract_AllowsCaseInsensitiveExactLocalWithMatchingStorage()
    {
        const string source = "float lOcAlStAtE\nBegin GameMode\nEnd";
        var decision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived: true,
            ScriptSourceTextOrigin.DmpFragment,
            [0x00, 0x1D, 0x00, 0x00],
            source,
            "Begin GameMode\nEnd",
            [new ScriptVariableInfo(1, "LocalState", 0)],
            [],
            isBigEndian: true);

        Assert.True(decision.ExecutableBundleSafe);
        Assert.Null(decision.SourceIssue);
        Assert.Equal(source, decision.SourceText);
    }

    [Theory]
    [InlineData("Begin GameMode\nEnd", "declaration count")]
    [InlineData("float RenamedLocal\nBegin GameMode\nEnd", "no unique exact SCTX declaration")]
    [InlineData("short ExactLocal\nBegin GameMode\nEnd", "storage does not match")]
    public void InlineContract_DropsSourceWhenLocalDeclarationDoesNotMatchInlineTable(
        string source,
        string expectedIssue)
    {
        var decision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived: true,
            ScriptSourceTextOrigin.DmpFragment,
            [0x00, 0x1D, 0x00, 0x00],
            source,
            "Begin GameMode\nEnd",
            [new ScriptVariableInfo(1, "ExactLocal", 0)],
            [],
            isBigEndian: true);

        Assert.True(decision.ExecutableBundleSafe);
        Assert.Null(decision.BundleIssue);
        Assert.Null(decision.SourceText);
        Assert.Contains(expectedIssue, decision.SourceIssue, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineContract_DropsSourceWhenInlineTableRepeatsScvrName()
    {
        const string source = "float ExactLocal\nfloat OtherLocal\nBegin GameMode\nEnd";
        var decision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived: true,
            ScriptSourceTextOrigin.DmpFragment,
            [0x00, 0x1D, 0x00, 0x00],
            source,
            "Begin GameMode\nEnd",
            [
                new ScriptVariableInfo(1, "ExactLocal", 0),
                new ScriptVariableInfo(2, "ExactLocal", 0),
            ],
            [],
            isBigEndian: true);

        Assert.True(decision.ExecutableBundleSafe);
        Assert.Null(decision.BundleIssue);
        Assert.Null(decision.SourceText);
        Assert.Contains("occurs more than once", decision.SourceIssue, StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneContract_CompiledWithoutSctxStillRequiresConsistentHeader()
    {
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(null, null));

        Assert.Null(decision.BundleIssue);
        Assert.Null(decision.Script.SourceText);
        Assert.False(decision.Script.IsIncompleteExecutableBundle);
    }

    [Theory]
    [InlineData(false, 3u)]
    [InlineData(true, 4u)]
    public void StandaloneContract_ShortOrStaleSchrFailsClosed(
        bool malformedHeader,
        uint compiledSize)
    {
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(null, null) with
            {
                HasSerializedHeader = !malformedHeader,
                HasMalformedSerializedHeader = malformedHeader,
                CompiledSize = compiledSize,
            });

        Assert.NotNull(decision.BundleIssue);
        Assert.True(decision.Script.IsIncompleteExecutableBundle);
    }

    [Fact]
    public void StandaloneContract_MalformedSerializedTableFailsEvenWhenSchrCountsAreZero()
    {
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(
            CompleteStandalone(null, null) with { HasMalformedSerializedTable = true });

        Assert.Contains("SLSD/SCVR/SCRO/SCRV", decision.BundleIssue, StringComparison.Ordinal);
        Assert.True(decision.Script.IsIncompleteExecutableBundle);
    }

    [Fact]
    public void StandaloneContract_SourceOnlyRecoveryIsExplicitlyUncompiled()
    {
        var decision = CapturedScriptEmissionContract.EvaluateStandalone(new ScriptRecord
        {
            FormId = 0x00123459,
            EditorId = "SourceOnly",
            IsCompiled = false,
            SourceText = "scn SourceOnly",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
        });

        Assert.Equal("scn SourceOnly", decision.Script.SourceText);
        Assert.False(decision.Script.IsCompiled);
        Assert.False(decision.Script.IsIncompleteExecutableBundle);
    }

    public void Dispose()
    {
        Logger.Instance.Reset();
        _output.Dispose();
    }

    private static ScriptRecord CompiledSource(string source, string decompiled) => new()
    {
        FormId = 0x00123456,
        EditorId = "DebugBCHostageScript",
        CompiledData = [0x00, 0x1D, 0x00, 0x00],
        IsBigEndian = true,
        IsCompiled = true,
        SourceText = source,
        SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
        DecompiledText = decompiled,
    };

    private static ScriptRecord CompleteStandalone(string? source, string? decompiled) => new()
    {
        FormId = 0x00123460,
        EditorId = "CapturedScript",
        HasSerializedHeader = true,
        CompiledSize = 4,
        CompiledData = [0x00, 0x1D, 0x00, 0x00],
        IsBigEndian = true,
        IsCompiled = true,
        SourceText = source,
        SourceTextOrigin = source is null
            ? ScriptSourceTextOrigin.None
            : ScriptSourceTextOrigin.DmpFragment,
        DecompiledText = decompiled,
    };
}
