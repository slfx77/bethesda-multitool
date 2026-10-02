using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class WithheldCapturedSourceTests
{
    [Fact]
    public void EmptySiblingCaptureDoesNotEraseWithheldSourceObservation()
    {
        var withheld = new DialogueResultScript { IsDmpDerived = true, WithheldSourceReason = "rejected correspondence" };
        var result = Assert.Single(DialogueResultScriptDuplicateMerger.Merge([[new()], [withheld]]));
        Assert.Equal(withheld.WithheldSourceReason, result.WithheldSourceReason);
        Assert.True(result.IsDmpDerived);
        Assert.True(result.HasContent);
    }

    [Fact]
    public void UnsafeCapturedInlineScript_KeepsWithheldReasonThroughMergesAndReports()
    {
        // A truncated instruction is unsafe in either byte order. SCTX is resident but cannot be accepted.
        var block = new DialogueResultScriptParser.DialogueResultScriptBuilder
        {
            SourceText = "scn Captured\r\nBegin GameMode\r\nEnd",
            CompiledData = [0x1D],
            HasSerializedHeader = true,
            ExpectedCompiledSize = 1
        };
        var parsed = Assert.Single(DialogueResultScriptParser.BuildResultScripts(
            [block], "Captured", 0x123, _ => null, isDmpDerived: true));
        Assert.Null(parsed.SourceText);
        Assert.NotNull(parsed.WithheldSourceReason);
        Assert.Contains("unsafe SCDA", parsed.WithheldSourceReason);
        Assert.True(parsed.HasContent);

        var merged = Assert.Single(DialogueResultScriptDuplicateMerger.Merge([[parsed], [parsed]]));
        Assert.Equal(parsed.WithheldSourceReason, merged.WithheldSourceReason);
        Assert.True(merged.IsDmpDerived);
        var info = new DialogueRecord { FormId = 0x123, ResultScripts = [merged] };
        var context = ConditionDisplayContext.ForResolver(FormIdResolver.Empty, BethesdaGame.FalloutNewVegas);
        var report = GeckDialogueWriter.GenerateDialogueReport([info], FormIdResolver.Empty, context, true);
        Assert.Contains("Source withheld", report);
        Assert.DoesNotContain(ScriptSourceProvenance.PartialDumpAbsenceWording, report);

        var terminal = new TerminalRecord
        {
            FormId = 0x456,
            MenuItems = [new TerminalMenuItem
            {
                Text = "Captured item", IsDmpDerived = true,
                WithheldSourceReason = merged.WithheldSourceReason,
                IsIncompleteExecutableBundle = true
            }]
        };
        var terminalReport = GeckTextContentWriter.GenerateTerminalsReport([terminal], FormIdResolver.Empty, context, true);
        Assert.Contains("Source withheld", terminalReport);
        Assert.DoesNotContain(ScriptSourceProvenance.PartialDumpAbsenceWording, terminalReport);
    }
}
