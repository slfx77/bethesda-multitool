using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     USER-AUTHORIZED reconstruction (2026-07-22): attaches a minimal, engine-valid result
///     script to a captured dialogue INFO whose writer script was not captured in the dump,
///     so an append-only quest local gains a structurally provable producer. This is a
///     deliberate, per-rule exception to the never-invent policy, made by explicit user
///     decision; each rule must cite its reconstruction evidence, the synthesis is logged
///     (code <c>dialog.synthesized-writer</c>), and a rule never replaces captured script
///     material — any existing bytecode or incomplete-bundle marker on the site disables it.
/// </summary>
internal static class DialogueWriterSynthesizer
{
    internal sealed record Rule(
        uint InfoFormId,
        uint QuestFormId,
        ushort SourceVariableIndex,
        string VariableName,
        string ScriptSource,
        string FallbackResponseText,
        string Evidence);

    /// <summary>
    ///     bUlyssesHired: the recruit-acceptance INFO 0x0013408B (topic 0x0013408A "Interested
    ///     in traveling with me?", CTDA GetIsID(Ulysses), Goodbye+RunImmediately flags) was
    ///     captured in xex21.dmp WITHOUT its result script — the runtime TESTopicInfo holds no
    ///     script data, no cached record image exists, and no "set …UlyssesHired" source text
    ///     appears anywhere in the dump. Every retail companion's recruit acceptance runs
    ///     exactly "set VNPCFollowers.b&lt;Name&gt;Hired to 1"; the proto VNPCFollowers script
    ///     declares bUlyssesHired at local index 24, and its captured consumers (the four
    ///     FollowersUlysses* PACKs and the Beyond-the-Beef companion variant) read it.
    /// </summary>
    public static IReadOnlyList<Rule> Rules { get; } =
    [
        new(
            InfoFormId: 0x0013408B,
            QuestFormId: 0x000B16D0,
            SourceVariableIndex: 24,
            VariableName: "bUlyssesHired",
            ScriptSource: "set VNPCFollowers.bUlyssesHired to 1",
            FallbackResponseText: "Let's go.",
            Evidence: "retail companion recruit pattern; proto VNPCFollowersQuestSCRIPT local 24; " +
                      "captured consumers: FollowersUlysses* PACKs, VMS18 companion variant"),
    ];

    public static void Apply(List<DialogueRecord> dialogues, IConversionProgressSink sink) =>
        Apply(dialogues, Rules, sink);

    internal static void Apply(
        List<DialogueRecord> dialogues,
        IReadOnlyList<Rule> rules,
        IConversionProgressSink sink)
    {
        foreach (var rule in rules)
        {
            var index = dialogues.FindIndex(info => info.FormId == rule.InfoFormId);
            if (index < 0)
            {
                sink.Info("Synthesizing dialogue writers",
                    $"Rule for {rule.VariableName}: site INFO 0x{rule.InfoFormId:X8} is not in " +
                    "this dump's captures — nothing synthesized.",
                    "INFO", rule.InfoFormId, "dialog.synthesized-writer-site-absent");
                continue;
            }

            var info = dialogues[index];
            if (info.ResultScripts.Any(static script =>
                    script.CompiledData is { Length: > 0 }
                    || script.IsIncompleteExecutableBundle
                    || !string.IsNullOrWhiteSpace(script.SourceText)))
            {
                sink.Info("Synthesizing dialogue writers",
                    $"Rule for {rule.VariableName}: site INFO 0x{rule.InfoFormId:X8} carries " +
                    "captured script material — synthesis skipped (captured data always wins).",
                    "INFO", rule.InfoFormId, "dialog.synthesized-writer-skipped");
                continue;
            }

            var script = new DialogueResultScript
            {
                SourceText = rule.ScriptSource,
                CompiledData = BuildSetQuestVariableBytecode(
                    scroIndex: 1, rule.SourceVariableIndex),
                ReferencedObjects = [rule.QuestFormId],
                IsBigEndianBytecode = false,
                IsDmpDerived = false,
            };
            var responses = info.Responses;
            if (!responses.Any(static response =>
                    DialogueInfoOverlayWriter.IsMeaningfulText(response.Text)))
            {
                responses =
                [
                    new DialogueResponse { ResponseNumber = 1, Text = rule.FallbackResponseText },
                ];
            }

            dialogues[index] = info with
            {
                ResultScripts = [script],
                Responses = responses,
                HasResultScript = true,
            };
            sink.Warn("Synthesizing dialogue writers",
                $"Synthesized result script on INFO 0x{rule.InfoFormId:X8}: " +
                $"\"{rule.ScriptSource}\" (quest 0x{rule.QuestFormId:X8} local " +
                $"{rule.VariableName} @ {rule.SourceVariableIndex}). AUTHORED content, not " +
                $"captured — evidence: {rule.Evidence}.",
                "INFO", rule.InfoFormId, "dialog.synthesized-writer",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["variable"] = rule.VariableName,
                    ["quest"] = $"0x{rule.QuestFormId:X8}",
                    ["source-index"] = rule.SourceVariableIndex.ToString(),
                    ["evidence"] = rule.Evidence,
                });
        }
    }

    /// <summary>
    ///     "set &lt;SCRO#scroIndex&gt;.&lt;local#variableIndex&gt; to 1" — byte-exact shape of the
    ///     retail GECK compiler's output, decoded from FalloutNV.esm INFO 0x0011759C
    ///     ("set VNPCFollowers.RexAvailable to 1" = 15 00 0A 00 72 02 00 73 10 00 02 00 20 31):
    ///     opcode 0x0015 Set, payload length, 0x72 referenced-object marker + SCRO index,
    ///     0x73 quest-local marker + variable index, expression length 2, expression " 1".
    /// </summary>
    internal static byte[] BuildSetQuestVariableBytecode(ushort scroIndex, ushort variableIndex) =>
    [
        0x15, 0x00,
        0x0A, 0x00,
        0x72, (byte)scroIndex, (byte)(scroIndex >> 8),
        0x73, (byte)variableIndex, (byte)(variableIndex >> 8),
        0x02, 0x00,
        0x20, 0x31,
    ];
}
