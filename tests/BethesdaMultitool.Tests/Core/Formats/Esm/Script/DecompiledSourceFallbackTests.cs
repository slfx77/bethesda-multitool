using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Script;

/// <summary>
///     USER RULING 2026-09-03: compiled scripts are preserved and converted to PC format, and
///     their source text is emitted if present or as the decompilation if not. Before this,
///     ~190 scripts per dump shipped with SCDA and no SCTX because the capture had no source
///     text resident (measured on xex44: SCDA 887 / SCTX 697).
/// </summary>
public class DecompiledSourceFallbackTests
{
    private const string Decompiled = """
                                      ScriptName MyTestSCRIPT
                                      Begin GameMode
                                        Set fRange to 512.0
                                        rTarget.Activate Player
                                      End
                                      """;

    private static readonly IReadOnlyList<ScriptVariableInfo> Variables =
    [
        new(1, "rTarget", 0),
        new(2, "fRange", 0),
        new(3, "bDoOnce", 1)
    ];

    [Fact]
    public void SynthesizedSource_DeclaresEveryLocalFromTheSlsdTable()
    {
        var text = CapturedScriptEmissionContract.BuildDecompiledSource(
            Decompiled, Variables, [0x80000001], "MyTestSCRIPT");

        Assert.NotNull(text);
        // SCRV binds local #1 as a reference. SLSD alone cannot distinguish ref from float,
        // and rendered source text is not independent evidence of a variable's storage class.
        Assert.Contains("ref rTarget", text, StringComparison.Ordinal);
        Assert.Contains("float fRange", text, StringComparison.Ordinal);
        Assert.Contains("short bDoOnce", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SynthesizedSource_KeepsScriptNameFirstAndBannerCommented()
    {
        var text = CapturedScriptEmissionContract.BuildDecompiledSource(
            Decompiled, Variables, "MyTestSCRIPT")!;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.All(lines.TakeWhile(l => !l.StartsWith("ScriptName", StringComparison.Ordinal)),
            line => Assert.StartsWith(";", line, StringComparison.Ordinal));
        Assert.Contains("ScriptName MyTestSCRIPT", lines);
        // The statements survive the rewrite — this is a rendering of the bytecode, not a stub.
        Assert.Contains(lines, l => l.Contains("rTarget.Activate", StringComparison.Ordinal));
    }

    [Fact]
    public void SynthesizedSource_SatisfiesTheDeclarationAuditItMustPass()
    {
        // The block is generated from the same SLSD/SCVR table that ships beside it, so the
        // captured-source consistency rules hold by construction. Proven by round-tripping the
        // synthesized text back through the contract as if it had been captured.
        var text = CapturedScriptEmissionContract.BuildDecompiledSource(
            Decompiled, Variables, "MyTestSCRIPT")!;

        var decision = CapturedScriptEmissionContract.EvaluateInline(
            true,
            ScriptSourceTextOrigin.DmpFragment,
            null,
            text,
            Decompiled,
            Variables,
            [],
            false,
            "MyTestSCRIPT");

        Assert.Equal(text, decision.SourceText);
    }

    [Fact]
    public void UnnamedLocal_ProducesNoDeclarationBlockButStillEmitsStatements()
    {
        IReadOnlyList<ScriptVariableInfo> unnamed = [new(1, null, 0), new(2, "fRange", 0)];

        var text = CapturedScriptEmissionContract.BuildDecompiledSource(
            Decompiled, unnamed, "MyTestSCRIPT")!;

        // A partial declaration block would contradict the SLSD count shipping beside it.
        Assert.DoesNotContain("float fRange\n", text, StringComparison.Ordinal);
        Assert.Contains("could not be declared", text, StringComparison.Ordinal);
        Assert.Contains("Begin GameMode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDecompilation_YieldsNoSynthesizedText()
    {
        Assert.Null(CapturedScriptEmissionContract.BuildDecompiledSource("", Variables, "X"));
        Assert.Null(CapturedScriptEmissionContract.BuildDecompiledSource(null, Variables, "X"));
    }
}
