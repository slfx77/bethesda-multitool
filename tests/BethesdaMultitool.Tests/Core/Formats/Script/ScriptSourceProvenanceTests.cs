using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     Script source text must never be labelled as something it is not: plugin SCTX is authored source, dump
///     text is recovered (or unattributed), and a decompilation is a BethesdaMultitool reconstruction. The
///     origin <c>None</c> is shared by plugin SCTX and unattributed dump text, so only the input kind separates
///     them. Every expected label and token is an independent literal.
/// </summary>
public sealed class ScriptSourceProvenanceTests
{
    private const string Source = "scn TestScript\r\nBegin GameMode\r\nEnd";

    [Theory]
    [InlineData("; Reconstruction from SCDA — BethesdaMultitool")]
    [InlineData("; === Decompiled from captured SCDA — no proven source text in the dump. ===")]
    [InlineData("; Decompiled from SCDA by BethesdaMultitool (reconstruction, not original source)")]
    public void Current_and_legacy_banners_keep_reconstruction_provenance(string banner)
    {
        var source = "\uFEFF \r\n" + banner + "\r\n" + Source;
        var script = new ScriptRecord { SourceText = source };

        var classification = ScriptSourceProvenance.Classify(script, false);

        Assert.Equal("decompiled-from-bytecode", classification.Token);
        Assert.Equal("Reconstruction (stored SCTX)", classification.Label);
        Assert.Null(ScriptSourceProvenance.AuthoredText(script));
        Assert.Equal(source, script.SourceText);
    }

    [Fact]
    public void ReopenedEmittedPlugin_DoesNotClaimItsReconstructionIsAuthored()
    {
        var emitted = CapturedScriptEmissionContract.BuildDecompiledSource("Begin GameMode\nEnd", [], "TestScript");
        Assert.NotNull(emitted);
        var script = new ScriptRecord { SourceText = "\uFEFF \r\n" + emitted };
        var classification = ScriptSourceProvenance.Classify(script, false);
        Assert.Equal(ScriptTextKind.ReconstructedDecompiled, classification.Kind);
        Assert.Equal("decompiled-from-bytecode", classification.Token);
        Assert.Equal("Reconstruction (stored SCTX)", classification.Label);
        Assert.Null(ScriptSourceProvenance.AuthoredText(script));
    }

    [Fact]
    public void WithheldCapturedSource_IsNotReportedAsAbsent()
    {
        var classification = ScriptSourceProvenance.Classify(
            new DialogueResultScript { IsDmpDerived = true, WithheldSourceReason = "unsafe SCDA bundle" }, true);
        Assert.Equal(ScriptTextKind.WithheldCaptured, classification.Kind);
        Assert.False(classification.HasSourceText);
        Assert.Contains("Source withheld (unsafe SCDA bundle)", classification.Label);
        Assert.DoesNotContain("not present", classification.Label);
    }

    [Fact]
    public void PluginSctxWithNoneOrigin_IsAuthoredPluginSource()
    {
        var script = new ScriptRecord { SourceText = Source, SourceTextOrigin = ScriptSourceTextOrigin.None };

        var classification = ScriptSourceProvenance.Classify(script, false);

        Assert.Equal(ScriptTextKind.AuthoredPlugin, classification.Kind);
        Assert.Equal("plugin-record", classification.Token);
        Assert.Equal("Source (SCTX)", classification.Label);
        Assert.Equal("not-applicable", classification.CorrespondenceToken);
        Assert.True(classification.IsAuthoredOriginal);
        Assert.True(classification.IsAuthorWrittenText);
        Assert.False(classification.IsReconstructed);
        Assert.False(classification.IsRecoveredFromDump);
        Assert.Equal(Source, ScriptSourceProvenance.AuthoredText(script));
    }

    [Fact]
    public void DumpTextWithNoneOrigin_IsUnattributed_NotAuthoredPluginSource()
    {
        var script = new ScriptRecord
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.None,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Unverified
        };

        var classification = ScriptSourceProvenance.Classify(script, true);

        Assert.Equal(ScriptTextKind.UnattributedDumpText, classification.Kind);
        Assert.Equal("unattributed-same-dump", classification.Token);
        Assert.Equal(
            "Recovered source (unattributed)",
            classification.Label);
        Assert.Equal("unverified", classification.CorrespondenceToken);
        Assert.False(classification.IsAuthoredOriginal);
        Assert.True(classification.IsRecoveredFromDump);
    }

    [Fact]
    public void RuntimeRecord_IsDumpTextEvenWhenTheCallerSaysPlugin()
    {
        var script = new ScriptRecord { SourceText = Source, FromRuntime = true };

        var classification = ScriptSourceProvenance.Classify(script, false);

        Assert.Equal(ScriptTextKind.UnattributedDumpText, classification.Kind);
        Assert.True(classification.IsMemoryDumpInput);
        Assert.False(classification.IsAuthoredOriginal);
    }

    [Theory]
    [InlineData(ScriptSourceCorrespondenceStatus.Accepted, "accepted")]
    [InlineData(ScriptSourceCorrespondenceStatus.AcceptedSourceOnly, "accepted-source-only")]
    [InlineData(ScriptSourceCorrespondenceStatus.Rejected, "rejected")]
    public void DmpFragment_IsRecoveredAndCarriesItsCorrespondence(
        ScriptSourceCorrespondenceStatus status,
        string expectedToken)
    {
        var script = new ScriptRecord
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            SourceTextCorrespondenceStatus = status
        };

        var classification = ScriptSourceProvenance.Classify(script, true);

        Assert.Equal(ScriptTextKind.RecoveredDmpFragment, classification.Kind);
        Assert.Equal("dmp-fragment", classification.Token);
        Assert.Equal(expectedToken, classification.CorrespondenceToken);
        Assert.Equal(
            $"Recovered source (dump fragment)",
            classification.Label);
        Assert.False(classification.IsAuthoredOriginal);
    }

    [Fact]
    public void RuntimeSameObject_IsRecoveredFromTheRuntimeObject()
    {
        var script = new ScriptRecord
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        };

        var classification = ScriptSourceProvenance.Classify(script, true);

        Assert.Equal(ScriptTextKind.RecoveredRuntimeObject, classification.Kind);
        Assert.Equal("runtime-same-object", classification.Token);
        Assert.Equal(
            "Recovered source (runtime object)",
            classification.Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecompiledFromBytecode_IsNeverAuthored(bool isMemoryDumpInput)
    {
        var script = new ScriptRecord
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode
        };

        var classification = ScriptSourceProvenance.Classify(script, isMemoryDumpInput);

        Assert.Equal(ScriptTextKind.ReconstructedDecompiled, classification.Kind);
        Assert.Equal("decompiled-from-bytecode", classification.Token);
        Assert.Equal(
            "Reconstruction (SCDA)",
            classification.Label);
        Assert.Equal("not-applicable", classification.CorrespondenceToken);
        Assert.False(classification.IsAuthoredOriginal);
        Assert.False(classification.IsAuthorWrittenText);
        Assert.True(classification.IsReconstructed);
        Assert.True(ScriptSourceProvenance.IsReconstructed(script));
        Assert.Null(ScriptSourceProvenance.AuthoredText(script));
    }

    [Fact]
    public void MissingSource_SaysSoAndNeverImpliesTheBuildLackedIt()
    {
        var script = new ScriptRecord { SourceText = null, CompiledData = [0x1D, 0x00] };

        var plugin = ScriptSourceProvenance.Classify(script, false);
        var dump = ScriptSourceProvenance.Classify(script, true);

        Assert.Equal(ScriptTextKind.None, plugin.Kind);
        Assert.Equal("none", plugin.Token);
        Assert.Equal("No source (SCTX)", plugin.Label);
        Assert.False(plugin.HasSourceText);
        Assert.Equal(ScriptTextKind.None, dump.Kind);
        Assert.Equal(
            "No source in capture",
            dump.Label);
    }

    [Fact]
    public void DialogueResultScript_UsesItsDumpFlagAndRecordsNoCorrespondence()
    {
        var plugin = new DialogueResultScript { SourceText = Source };
        var recovered = new DialogueResultScript
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            IsDmpDerived = true
        };
        var unattributed = new DialogueResultScript { SourceText = Source, IsDmpDerived = true };

        Assert.Equal(ScriptTextKind.AuthoredPlugin, ScriptSourceProvenance.Classify(plugin, false).Kind);

        var recoveredClassification = ScriptSourceProvenance.Classify(recovered, false);
        Assert.Equal(ScriptTextKind.RecoveredDmpFragment, recoveredClassification.Kind);
        Assert.Equal("not-recorded", recoveredClassification.CorrespondenceToken);
        Assert.Equal("Recovered source (dump fragment)", recoveredClassification.Label);

        Assert.Equal(ScriptTextKind.UnattributedDumpText, ScriptSourceProvenance.Classify(unattributed, false).Kind);
    }

    [Fact]
    public void TerminalMenuItem_ClassifiesLikeTheOtherEmbeddedScripts()
    {
        var plugin = new TerminalMenuItem { SourceText = Source };
        var reconstructed = new TerminalMenuItem
        {
            SourceText = Source,
            SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
            IsDmpDerived = true
        };

        Assert.Equal(ScriptTextKind.AuthoredPlugin, ScriptSourceProvenance.Classify(plugin, false).Kind);
        Assert.Equal(ScriptTextKind.UnattributedDumpText, ScriptSourceProvenance.Classify(plugin, true).Kind);
        Assert.Equal(
            ScriptTextKind.ReconstructedDecompiled,
            ScriptSourceProvenance.Classify(reconstructed, false).Kind);
        Assert.Equal(ScriptTextKind.None, ScriptSourceProvenance.Classify(new TerminalMenuItem(), true).Kind);
    }

    [Fact]
    public void DecompiledTextLabel_AndAbsenceWording_AreTheAgreedText()
    {
        Assert.Equal(
            "Reconstruction (SCDA)",
            ScriptSourceProvenance.DecompiledTextLabel);
        Assert.Equal(
            "not present in this capture; absence from a partial memory dump is not evidence of absence from " +
            "the build",
            ScriptSourceProvenance.PartialDumpAbsenceWording);
    }
}
