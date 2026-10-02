using System.Globalization;
using System.Text;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Show;

/// <summary>
///     <c>show --full</c> (alias <c>--no-truncate</c>) and the default truncation marker.
///     <para>
///         Before this, <c>show</c> cut SCPT source and decompiled text, MESG text and BOOK text at 2,000
///         characters with a bare "... (truncated)", and every body sat inside a Spectre panel, which folds
///         each line at the console width (80 when output is redirected, even under <c>--plain</c>) and
///         measures a TAB as one cell. Retail FalloutNV.esm has 366 scripts whose SCTX exceeds the cap
///         (BooneSCRIPT ~2,923; VEFR01NCRGood2QuestSCRIPT ~32,044 with a 317-column line), so the only way to
///         read them was the aggregate script report. Under <c>--full</c> each body is written after the
///         panel as a BEGIN/END block through the console's raw writer: no cap, no folding, TABs and CRLF
///         kept. Every script body is also labelled by its provenance, never as "Source (SCTX)" regardless of
///         where the text came from.
///     </para>
///     <para>
///         Each render goes through <see cref="ShowCommand.TryRender" /> into a private capture console, so
///         the real renderer chain runs and no global console is swapped.
///     </para>
/// </summary>
public sealed class ShowFullTextTests
{
    private const uint ScriptFormId = 0x00AB0200;
    private const uint LinkedFormId = 0x00AB0001;
    private const int SourceLength = 5432;
    private const int DecompiledLength = 2600;
    private const int LongTextLength = 3000;
    private const string SourceSentinel = "CUT_BRANCH_SENTINEL";
    private const string DecompiledSentinel = "DECOMPILED_TAIL_SENTINEL";
    private const string TextSentinel = "TEXT_TAIL_SENTINEL";
    private const string AuthoredLabel = "Source (SCTX)";

    private const string DecompiledLabel =
        "Reconstruction (SCDA)";

    private const string AbsenceWording =
        "not present in this capture; absence from a partial memory dump is not evidence of absence from the build";

    /// <summary>The width a redirected console reports: every panel line folds here.</summary>
    private const int RedirectedWidth = 80;

    /// <summary>Wide enough that no panel line wraps, so each phrase can be matched whole.</summary>
    private const int WideConsole = 400;

    [Fact]
    public void Script_Full_EmitsSourceAndDecompiledTextVerbatim()
    {
        var script = BuildLongScript();
        var source = script.SourceText!;
        var decompiled = script.DecompiledText!;
        var records = new RecordCollection { Scripts = [script] };

        // At the redirected width: a panel would fold the 300-character line and the long comment lines,
        // so the exact-substring checks below can only pass if the bodies bypass the panel.
        var output = Render(records, FormIdResolver.Empty, ScriptFormId, true, width: RedirectedWidth);

        Assert.Contains(source, output, StringComparison.Ordinal);
        Assert.Contains(decompiled, output, StringComparison.Ordinal);
        Assert.Contains(SourceSentinel, output, StringComparison.Ordinal);
        Assert.Contains(DecompiledSentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain("(truncated", output, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(output, "----- BEGIN "));
        Assert.Equal(2, CountOccurrences(output, "----- END "));

        // Each block is delimited exactly: header with the size, the text as stored, the closing line.
        var nl = Environment.NewLine;
        Assert.Contains(
            $"----- BEGIN {AuthoredLabel} (5,432 chars, {CountLineFeeds(source)} lines) -----{nl}{source}" +
            $"----- END {AuthoredLabel} -----{nl}",
            output, StringComparison.Ordinal);
        Assert.Contains(
            $"----- BEGIN {DecompiledLabel} (2,600 chars, {CountLineFeeds(decompiled)} lines) -----{nl}{decompiled}" +
            $"----- END {DecompiledLabel} -----{nl}",
            output, StringComparison.Ordinal);

        // The panel comes first and stands in for each body with its size.
        var wide = Render(records, FormIdResolver.Empty, ScriptFormId, true, width: WideConsole);
        Assert.Contains($"{AuthoredLabel}: (5,432 chars; printed verbatim below)", wide, StringComparison.Ordinal);
        Assert.Contains($"{DecompiledLabel}: (2,600 chars; printed verbatim below)", wide, StringComparison.Ordinal);
        var panelAt = wide.IndexOf("SCPT FullTextProbeSCRIPT", StringComparison.Ordinal);
        var firstBlockAt = wide.IndexOf("----- BEGIN ", StringComparison.Ordinal);
        Assert.True(panelAt >= 0 && panelAt < firstBlockAt, "The verbatim blocks must follow the panel.");
    }

    [Fact]
    public void Script_Default_TruncationMarkerStatesTotalAndFullOption()
    {
        var script = BuildLongScript();

        var output = Render(new RecordCollection { Scripts = [script] }, FormIdResolver.Empty, ScriptFormId, false);

        Assert.DoesNotContain(SourceSentinel, output, StringComparison.Ordinal);
        Assert.DoesNotContain(DecompiledSentinel, output, StringComparison.Ordinal);
        Assert.Contains("... (truncated: 2,000 of 5,432 characters shown; rerun with --full)", output,
            StringComparison.Ordinal);
        Assert.Contains("... (truncated: 2,000 of 2,600 characters shown; rerun with --full)", output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("----- BEGIN", output, StringComparison.Ordinal);

        // The part inside the cap still renders, markup-like text literally.
        Assert.Contains($"{AuthoredLabel}:", output, StringComparison.Ordinal);
        Assert.Contains("; [1] [red]not markup[/]", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The heading over a script's source text says where the text came from. Only plugin SCTX read from a
    ///     plugin is "authored; stored in this plugin"; dump text says it came from a dump (with its
    ///     correspondence verdict), and a decompilation is a reconstruction. The decompiled body is always a
    ///     BethesdaMultitool reconstruction. Before, every body was headed "Source (SCTX):".
    /// </summary>
    [Theory]
    [InlineData(ScriptSourceTextOrigin.None, false, false, ScriptSourceCorrespondenceStatus.Unverified,
        "Source (SCTX)", true)]
    [InlineData(ScriptSourceTextOrigin.None, true, false, ScriptSourceCorrespondenceStatus.Accepted,
        "Recovered source (unattributed)", false)]
    [InlineData(ScriptSourceTextOrigin.None, false, true, ScriptSourceCorrespondenceStatus.Unverified,
        "Recovered source (unattributed)", false)]
    [InlineData(ScriptSourceTextOrigin.DmpFragment, false, true, ScriptSourceCorrespondenceStatus.Accepted,
        "Recovered source (dump fragment)", false)]
    [InlineData(ScriptSourceTextOrigin.DmpFragment, false, true, ScriptSourceCorrespondenceStatus.Rejected,
        "Recovered source (dump fragment)", false)]
    [InlineData(ScriptSourceTextOrigin.RuntimeSameObject, true, true,
        ScriptSourceCorrespondenceStatus.AcceptedSourceOnly,
        "Recovered source (runtime object)", false)]
    [InlineData(ScriptSourceTextOrigin.DecompiledFromBytecode, false, false,
        ScriptSourceCorrespondenceStatus.Unverified,
        "Reconstruction (SCDA)",
        false)]
    [InlineData(ScriptSourceTextOrigin.DecompiledFromBytecode, true, true, ScriptSourceCorrespondenceStatus.Accepted,
        "Reconstruction (SCDA)",
        false)]
    public void Script_LabelsSourceProvenance(ScriptSourceTextOrigin origin, bool fromRuntime, bool dumpInput,
        ScriptSourceCorrespondenceStatus correspondence, string expectedLabel, bool expectStoredPluginSource)
    {
        var script = new ScriptRecord
        {
            FormId = ScriptFormId,
            EditorId = "ProvenanceProbeSCRIPT",
            SourceText = "scn ProvenanceProbeSCRIPT\r\nbegin GameMode\r\nend\r\n",
            SourceTextOrigin = origin,
            SourceTextCorrespondenceStatus = correspondence,
            DecompiledText = "ScriptName ProvenanceProbeSCRIPT\nBegin GameMode\nEnd\n",
            FromRuntime = fromRuntime
        };
        var records = new RecordCollection { Scripts = [script] };

        var byDefault = Render(records, FormIdResolver.Empty, ScriptFormId, false, dumpInput);
        var full = Render(records, FormIdResolver.Empty, ScriptFormId, true, dumpInput);

        Assert.Contains($"{expectedLabel}:", byDefault, StringComparison.Ordinal);
        Assert.Contains($"----- BEGIN {expectedLabel} (", full, StringComparison.Ordinal);
        foreach (var output in new[] { byDefault, full })
        {
            Assert.Equal(expectStoredPluginSource,
                output.Contains("Source (SCTX)", StringComparison.Ordinal));
            Assert.DoesNotContain("authored; stored in this plugin", output, StringComparison.Ordinal);
            Assert.Contains(DecompiledLabel, output, StringComparison.Ordinal);
            Assert.DoesNotContain("Decompiled (SCDA):", output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A script without source text used to print nothing where the source would be. It now says so, and
    ///     for a memory dump it adds that the capture's gap is not evidence the build lacked the text.
    /// </summary>
    [Theory]
    [InlineData(false, "No source (SCTX)")]
    [InlineData(true, "No source in capture")]
    public void Script_WithoutSource_SaysSoWithoutImplyingTheBuildLackedIt(bool dumpInput, string expected)
    {
        var script = new ScriptRecord
        {
            FormId = ScriptFormId,
            EditorId = "NoSourceProbeSCRIPT",
            DecompiledText = "ScriptName NoSourceProbeSCRIPT\nBegin GameMode\nEnd\n"
        };

        foreach (var fullText in new[] { false, true })
        {
            var output = Render(new RecordCollection { Scripts = [script] }, FormIdResolver.Empty, ScriptFormId,
                fullText, dumpInput);

            Assert.Contains(expected, output, StringComparison.Ordinal);
            Assert.DoesNotContain("----- BEGIN Source", output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The TG14 header lines: the SCHR compiled flag, and the byte order the SCDA was decoded in with the
    ///     rule that chose it — which is not the container's order (an Xbox 360 ESM is a big-endian container
    ///     whose serialized SCDA is little-endian). A missing SCDA from a memory dump is not called absent
    ///     from the build.
    /// </summary>
    [Fact]
    public void Script_ShowsCompiledFlagAndBytecodeOrder()
    {
        var xbox = new ScriptRecord
        {
            FormId = ScriptFormId,
            EditorId = "OrderProbeSCRIPT",
            IsCompiled = true,
            IsBigEndian = true,
            IsBigEndianBytecode = false,
            BytecodeByteOrderEvidence = ScriptBytecodeByteOrderEvidence.ScriptNameAnchor,
            CompiledSize = 8,
            CompiledData = [0x1D, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00]
        };
        var uncompiled = new ScriptRecord { FormId = ScriptFormId, EditorId = "NoScdaProbeSCRIPT" };

        var xboxOutput = Render(new RecordCollection { Scripts = [xbox] }, FormIdResolver.Empty, ScriptFormId, false);
        var pluginOutput =
            Render(new RecordCollection { Scripts = [uncompiled] }, FormIdResolver.Empty, ScriptFormId, false);
        var dumpOutput = Render(new RecordCollection { Scripts = [uncompiled] }, FormIdResolver.Empty, ScriptFormId,
            false, true);

        Assert.Contains("Compiled flag: True", xboxOutput, StringComparison.Ordinal);
        Assert.Contains("Bytecode:   Little-Endian (ScriptName anchor)", xboxOutput, StringComparison.Ordinal);
        Assert.Contains("Compiled flag: False", pluginOutput, StringComparison.Ordinal);
        Assert.Contains("Bytecode:   (no SCDA)", pluginOutput, StringComparison.Ordinal);
        Assert.Contains($"Bytecode:   no SCDA: {AbsenceWording}", dumpOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_Full_ListsVariablesAndReferencedObjects()
    {
        var script = new ScriptRecord
        {
            FormId = ScriptFormId,
            EditorId = "RefProbeSCRIPT",
            VariableCount = 21,
            RefObjectCount = 2,
            Variables = [new ScriptVariableInfo(3, "bHired", 1), new ScriptVariableInfo(0, "fTimer", 0)],
            ReferencedObjects = [0x00000014, LinkedFormId],
            SourceText = "scn RefProbeSCRIPT\r\n"
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [LinkedFormId] = "Bracket[x]Ref" },
            new Dictionary<uint, string>(),
            new Dictionary<uint, uint>());
        var records = new RecordCollection { Scripts = [script] };

        var full = Render(records, resolver, ScriptFormId, true);
        var byDefault = Render(records, resolver, ScriptFormId, false);

        // The SCHR count and the parsed declarations are different quantities; both are shown.
        Assert.Contains("Variables (2 listed; SCHR VariableCount 21):", full, StringComparison.Ordinal);
        var timer = full.IndexOf("[  0] float fTimer", StringComparison.Ordinal);
        var hired = full.IndexOf("[  3] int   bHired", StringComparison.Ordinal);
        Assert.True(timer >= 0 && hired > timer, "Variables must be listed in index order.");

        Assert.Contains("Referenced Objects (2):", full, StringComparison.Ordinal);
        Assert.Contains("[  1] PlayerRef (0x00000014)", full, StringComparison.Ordinal);
        Assert.Contains("[  2] Bracket[x]Ref (0x00AB0001)", full, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", full, StringComparison.Ordinal);

        // The default panel is unchanged: the lists are a --full addition.
        Assert.DoesNotContain("Referenced Objects", byDefault, StringComparison.Ordinal);
        Assert.DoesNotContain("SCHR VariableCount", byDefault, StringComparison.Ordinal);
    }

    /// <summary>
    ///     No retail FNV book or message exceeds the cap (the longest book text is 48 characters), so this is
    ///     the only coverage of their full-text path.
    /// </summary>
    [Fact]
    public void MessageAndBook_Full_EmitCompleteText()
    {
        var text = BuildLongText();
        var records = new RecordCollection
        {
            Messages = [new MessageRecord { FormId = 0x00AB0300, EditorId = "LongMessage", Description = text }],
            Books = [new BookRecord { FormId = 0x00AB0301, EditorId = "LongBook", Text = text }]
        };

        foreach (var (formId, blockTitle) in new[] { (0x00AB0300u, "Message text"), (0x00AB0301u, "Book text") })
        {
            var byDefault = Render(records, FormIdResolver.Empty, formId, false);
            var full = Render(records, FormIdResolver.Empty, formId, true, width: RedirectedWidth);

            Assert.DoesNotContain(TextSentinel, byDefault, StringComparison.Ordinal);
            Assert.Contains("... (truncated: 2,000 of 3,000 characters shown; rerun with --full)", byDefault,
                StringComparison.Ordinal);

            Assert.Contains(text, full, StringComparison.Ordinal);
            Assert.DoesNotContain("(truncated", full, StringComparison.Ordinal);
            Assert.Contains($"----- BEGIN {blockTitle} (3,000 chars, ", full, StringComparison.Ordinal);
            Assert.Contains($"----- END {blockTitle} -----", full, StringComparison.Ordinal);
            Assert.Contains("Text: (3,000 chars; printed verbatim below)", full, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The shared detail panel (NPC_, QUST, PACK, DIAL, CELL, WRLD and the INFO/TERM detail) has no cap of
    ///     its own, but a panel folds a multi-line or long value at the console width and reflows its TABs.
    ///     Under <c>--full</c> such a value is replaced by its length and written verbatim after the panel,
    ///     titled by its section and label; short single-line values stay in the panel, and the default mode
    ///     is unchanged.
    /// </summary>
    [Fact]
    public void SharedDetail_Full_WritesMultilineValuesVerbatim()
    {
        const string resultScript = "set x to 1\r\n\tif y == 2\r\n\t\tdisable\r\n\tendif\r\n";
        const string responses = "line one\nline two";
        var longValue = new string('L', 2500);
        var model = new RecordDetailModel
        {
            RecordSignature = "INFO",
            FormId = 0x00AB0500,
            EditorId = "DetailProbe",
            Sections =
            [
                new RecordDetailSection
                {
                    Title = "Identity",
                    Entries =
                    [
                        new RecordDetailEntry
                            { Kind = RecordDetailEntryKind.Scalar, Label = "Form ID", Value = "0x00AB0500" },
                        new RecordDetailEntry
                            { Kind = RecordDetailEntryKind.CodeBlock, Label = "Result Script", Value = resultScript },
                        new RecordDetailEntry
                            { Kind = RecordDetailEntryKind.Scalar, Label = "Long", Value = longValue }
                    ]
                },
                new RecordDetailSection
                {
                    Title = "Responses",
                    Entries =
                    [
                        new RecordDetailEntry
                        {
                            Kind = RecordDetailEntryKind.List,
                            Label = "Responses",
                            Items =
                            [
                                new RecordDetailListItem { Label = "[1]", Value = responses },
                                new RecordDetailListItem { Label = "[2]", Value = "short" }
                            ]
                        }
                    ]
                }
            ]
        };

        var full = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = RedirectedWidth;
            SharedRecordDetailShowRenderer.Render(console, model, true);
        });
        var byDefault = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            SharedRecordDetailShowRenderer.Render(console, model);
        });

        var nl = Environment.NewLine;
        Assert.Contains(
            $"----- BEGIN Identity / Result Script (43 chars, 4 lines) -----{nl}{resultScript}" +
            $"----- END Identity / Result Script -----{nl}",
            full, StringComparison.Ordinal);
        Assert.Contains($"----- BEGIN Identity / Long (2,500 chars, 1 lines) -----{nl}{longValue}{nl}", full,
            StringComparison.Ordinal);
        Assert.Contains($"----- BEGIN Responses / [1] (17 chars, 2 lines) -----{nl}{responses}{nl}", full,
            StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(full, "----- BEGIN "));
        Assert.Contains("Result Script: (43 chars; printed verbatim below)", full, StringComparison.Ordinal);
        Assert.Contains("Long: (2,500 chars; printed verbatim below)", full, StringComparison.Ordinal);
        Assert.Contains("[1]: (17 chars; printed verbatim below)", full, StringComparison.Ordinal);
        Assert.Contains("Form ID: 0x00AB0500", full, StringComparison.Ordinal);
        Assert.Contains("[2]: short", full, StringComparison.Ordinal);

        Assert.DoesNotContain("----- BEGIN", byDefault, StringComparison.Ordinal);
        Assert.DoesNotContain("printed verbatim below", byDefault, StringComparison.Ordinal);
        Assert.Contains("Result Script: set x to 1", byDefault, StringComparison.Ordinal);
        // Default mode keeps the value in the panel; Spectre moves an unbroken 2,500-char word onto its own line.
        Assert.Contains("Long:", byDefault, StringComparison.Ordinal);
        Assert.Contains(new string('L', 100), byDefault, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The same path end to end: a QUST (claimed by the shared renderer, first in the chain) whose
    ///     objective text spans lines is written verbatim under <c>--full</c>, so the flag reaches the shared
    ///     renderer through the render context.
    /// </summary>
    [Fact]
    public void SharedDetail_Full_ReachesTheSharedRendererThroughShow()
    {
        const string objective = "Find the courier.\r\n\tThen report back.";
        var quest = new QuestRecord
        {
            FormId = 0x00AB0600,
            EditorId = "FullTextProbeQuest",
            Objectives = [new QuestObjective { Index = 10, DisplayText = objective }]
        };
        var records = new RecordCollection { Quests = [quest] };

        var full = Render(records, FormIdResolver.Empty, 0x00AB0600, true, width: RedirectedWidth);
        var byDefault = Render(records, FormIdResolver.Empty, 0x00AB0600, false);

        Assert.Contains($"----- BEGIN Objectives / [10] (37 chars, 2 lines) -----{Environment.NewLine}{objective}",
            full, StringComparison.Ordinal);
        Assert.DoesNotContain("----- BEGIN", byDefault, StringComparison.Ordinal);
    }

    private static string Render(RecordCollection records, FormIdResolver resolver, uint formId, bool fullText,
        bool isMemoryDumpInput = false, int width = WideConsole)
    {
        var rendered = false;
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = width;
            rendered = ShowCommand.TryRender(records, resolver, formId, null,
                new ShowRenderContext(console, fullText, isMemoryDumpInput));
        });

        Assert.True(rendered, "No show renderer claimed the record.");
        return output;
    }

    /// <summary>
    ///     A SCPT whose source is exactly <see cref="SourceLength" /> characters with CRLF line breaks, TABs, a
    ///     300-character unbroken token and markup-like text inside the cap, and <see cref="SourceSentinel" />
    ///     past it; its decompiled text is exactly <see cref="DecompiledLength" /> characters with LF breaks and
    ///     <see cref="DecompiledSentinel" /> past the cap.
    /// </summary>
    private static ScriptRecord BuildLongScript()
    {
        var source = new StringBuilder();
        source.Append("scn FullTextProbeSCRIPT\r\n\r\n");
        source.Append("short\tFollowerSwitchAggressive\t\t\t; 0 = Passive, 1 = Aggressive\r\n");
        source.Append("; [1] [red]not markup[/]\r\n");
        source.Append("; ").Append('W', 300).Append("\r\n");
        source.Append("begin GameMode\r\n");
        for (var i = 0; source.Length < 4480; i++)
        {
            source.Append($"\tif (VNPCFollowers.nCurrentFollowers == {i})\r\n\t\tset iLine to {i}\r\n\tendif\r\n");
        }

        source.Append("\t; ").Append(SourceSentinel).Append("\r\n");
        var sourceText = PadTo(source, SourceLength, "\r\nend\r\n");

        var decompiled = new StringBuilder();
        decompiled.Append("ScriptName FullTextProbeSCRIPT\nBegin GameMode\n");
        for (var i = 0; decompiled.Length < 2380; i++)
        {
            decompiled.Append($"\tIf (VNPCFollowers.nCurrentFollowers == {i})\n\t\tSet iLine To {i}\n\tEndIf\n");
        }

        decompiled.Append("\t; ").Append(DecompiledSentinel).Append('\n');
        var decompiledText = PadTo(decompiled, DecompiledLength, "\nEnd\n");

        Assert.Equal(SourceLength, sourceText.Length);
        Assert.Equal(DecompiledLength, decompiledText.Length);
        Assert.True(sourceText.IndexOf(SourceSentinel, StringComparison.Ordinal) > 2000);
        Assert.True(decompiledText.IndexOf(DecompiledSentinel, StringComparison.Ordinal) > 2000);

        return new ScriptRecord
        {
            FormId = ScriptFormId,
            EditorId = "FullTextProbeSCRIPT",
            IsQuestScript = true,
            IsCompiled = true,
            VariableCount = 1,
            SourceText = sourceText,
            DecompiledText = decompiledText
        };
    }

    /// <summary>
    ///     Exactly <see cref="LongTextLength" /> characters of CRLF-broken, TAB-indented prose with
    ///     <see cref="TextSentinel" /> past the cap.
    /// </summary>
    private static string BuildLongText()
    {
        var text = new StringBuilder();
        for (var i = 0; text.Length < 2450; i++)
        {
            text.Append($"Entry {i}:\tThe wasteland stretches on, and the road is long.\r\n");
        }

        text.Append(TextSentinel).Append("\r\n");
        var result = PadTo(text, LongTextLength, "\r\nThe end.");
        Assert.Equal(LongTextLength, result.Length);
        return result;
    }

    /// <summary>Fill <paramref name="sb" /> with a comment line so it ends with <paramref name="tail" /> at exactly <paramref name="length" />.</summary>
    private static string PadTo(StringBuilder sb, int length, string tail)
    {
        var fill = length - sb.Length - tail.Length;
        Assert.True(fill >= 2, "The fixture body overran its target length.");
        sb.Append(';').Append('-', fill - 1).Append(tail);
        return sb.ToString();
    }

    /// <summary>Line feeds in a fixture that ends with one, i.e. its line count, as the block header prints it.</summary>
    private static string CountLineFeeds(string text)
    {
        return text.Count(c => c == '\n').ToString("N0", CultureInfo.InvariantCulture);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
