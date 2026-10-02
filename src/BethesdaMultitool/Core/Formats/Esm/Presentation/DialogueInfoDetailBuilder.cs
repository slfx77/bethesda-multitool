using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>
///     Builds the INFO (dialogue response) detail model the CLI <c>show</c> command renders: identity,
///     relationships, the serialized DATA fields, every condition, every response in full, topic links and both
///     result-script slots.
///     <para>
///         Conditions come from the shared <see cref="ConditionDescriber" />, so an INFO prints a condition exactly
///         as a PACK or a terminal does. Result-script text is labelled by <see cref="ScriptSourceProvenance" />:
///         authored SCTX, text recovered from a memory dump, and a BethesdaMultitool reconstruction each keep their
///         own heading, and decompiled text is always shown separately under
///         <see cref="ScriptSourceProvenance.DecompiledTextLabel" />. Text is never truncated here.
///     </para>
///     <para>
///         The GUI's dialogue viewer builds its own INFO panel (<c>DialogueRecordDetailBuilder</c>); this model is
///         reached only through <see cref="RecordDetailPresenter.TryBuildForLookup" />.
///     </para>
/// </summary>
internal static class DialogueInfoDetailBuilder
{
    /// <summary>The wording for a result-script slot that holds no bytecode.</summary>
    internal const string NoCompiledCode = "no compiled code";

    /// <summary>High bit the parsers set on an SCRV (local variable) entry of a reference table.</summary>
    private const uint ScrvLocalMarker = 0x80000000;

    /// <summary>
    ///     Builds the model.
    /// </summary>
    /// <param name="info">The INFO record.</param>
    /// <param name="records">The collection it came from (for the parent topic's name and type), or null.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="conditions">Condition context: the game (also used to name DATA fields) and quest variables.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when the input is a memory dump: script text is then never labelled as plugin SCTX, and a missing
    ///     script is described as absent from the capture, not from the build.
    /// </param>
    internal static RecordDetailModel Build(
        DialogueRecord info,
        RecordCollection? records,
        FormIdResolver resolver,
        ConditionDisplayContext conditions,
        bool isMemoryDumpInput)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(conditions);

        var sections = new List<RecordDetailSection>
        {
            BuildIdentity(info),
            BuildRelationships(info, records, resolver),
            BuildData(info, conditions.Game),
            RecordDetailBuilders.BuildConditionsSection(info.Conditions, conditions),
            BuildResponses(info, resolver),
            BuildLinks(info, resolver),
            BuildResultScripts(info, resolver, isMemoryDumpInput)
        };

        return RecordDetailHelpers.Model("INFO", info.FormId, info.EditorId, null, sections);
    }

    private static RecordDetailSection BuildIdentity(DialogueRecord info)
    {
        return RecordDetailHelpers.Section("Identity",
        [
            RecordDetailHelpers.Scalar("Form ID", $"0x{info.FormId:X8}"),
            RecordDetailHelpers.Scalar("Editor ID", info.EditorId ?? "(none)"),
            RecordDetailHelpers.Scalar("Offset", $"0x{info.Offset:X8}"),
            RecordDetailHelpers.Scalar("Raw Record Offset",
                info.RawRecordOffset > 0 && info.RawRecordOffset != info.Offset
                    ? $"0x{info.RawRecordOffset:X8}"
                    : null),
            RecordDetailHelpers.Scalar("Runtime Struct Offset",
                info.RuntimeStructOffset > 0 ? $"0x{info.RuntimeStructOffset:X8}" : null),
            RecordDetailHelpers.Scalar("Container Byte Order",
                info.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")
        ]);
    }

    private static RecordDetailSection BuildRelationships(
        DialogueRecord info,
        RecordCollection? records,
        FormIdResolver resolver)
    {
        var entries = new List<RecordDetailEntry>();
        AddFormLink(entries, "Topic", info.TopicFormId, resolver);
        if (records is not null && info.TopicFormId is { } topicFormId && topicFormId != 0 &&
            records.DialogTopics.FirstOrDefault(topic => topic.FormId == topicFormId) is { } parent)
        {
            entries.Add(RecordDetailHelpers.Scalar(DialogueTopicLabels.IsGreeting(parent.EditorId)
                ? "Shared topic text" : "Topic Name", parent.FullName));
            entries.Add(RecordDetailHelpers.Scalar("Topic Type", parent.TopicTypeName));
        }

        AddFormLink(entries, "Quest", info.QuestFormId, resolver);
        AddFormLink(entries, "Speaker", info.SpeakerFormId, resolver);
        AddFormLink(entries, "Speaker Faction", info.SpeakerFactionFormId, resolver);
        AddFormLink(entries, "Speaker Race", info.SpeakerRaceFormId, resolver);
        AddFormLink(entries, "Speaker Voice Type", info.SpeakerVoiceTypeFormId, resolver);
        AddFormLink(entries, "Speaker Animation", info.SpeakerAnimationFormId, resolver);
        AddFormLink(entries, "Previous INFO", info.PreviousInfo, resolver);
        AddFormLink(entries, "Speech Challenge Skill/Perk", info.PerkSkillStatFormId, resolver);
        return RecordDetailHelpers.Section("Relationships", entries);
    }

    /// <summary>
    ///     The serialized INFO DATA fields as (label, value) pairs, named from the game's generated schema:
    ///     <c>Type: Conversation (1)</c>, <c>Next Speaker: Target (0)</c>, <c>Flags 1: 0x05 (Goodbye, Say Once)</c>,
    ///     <c>Flags 2: ...</c> (a bit the schema does not name prints as <c>bit N</c>), then the stored length, and a
    ///     note when the game has no INFO DATA schema (values then print unnamed). Fields the DATA did not reach are
    ///     omitted. Shared so every surface that prints INFO DATA names a bit the same way. Plain text.
    /// </summary>
    internal static IReadOnlyList<(string Label, string Value)> DescribeSerializedInfoData(
        InfoSerializedData data,
        BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(data);

        var fields = TryGetInfoDataFields(game);
        var pairs = new List<(string Label, string Value)>(6)
        {
            (FieldName(fields, InfoSerializedData.TypeOffset, "Type"),
                FormatEnum(data.InfoType, fields?[InfoSerializedData.TypeOffset]))
        };
        if (data.NextSpeaker is { } nextSpeaker)
        {
            pairs.Add((FieldName(fields, InfoSerializedData.NextSpeakerOffset, "Next Speaker"),
                FormatEnum(nextSpeaker, fields?[InfoSerializedData.NextSpeakerOffset])));
        }

        if (data.Flags1 is { } flags1)
        {
            pairs.Add((FieldName(fields, InfoSerializedData.Flags1Offset, "Flags 1"),
                FormatFlags(flags1, fields?[InfoSerializedData.Flags1Offset])));
        }

        if (data.Flags2 is { } flags2)
        {
            pairs.Add((FieldName(fields, InfoSerializedData.Flags2Offset, "Flags 2"),
                FormatFlags(flags2, fields?[InfoSerializedData.Flags2Offset])));
        }

        pairs.Add(("DATA Length", DescribeDataLength(data)));
        if (fields is null)
        {
            pairs.Add(("DATA Names", $"no generated INFO DATA schema for {game}; values are shown unnamed"));
        }

        return pairs;
    }

    private static RecordDetailSection BuildData(DialogueRecord info, BethesdaGame game)
    {
        var fields = TryGetInfoDataFields(game);
        var entries = new List<RecordDetailEntry>();
        var data = info.SerializedInfoData;
        if (data is not null)
        {
            entries.AddRange(DescribeSerializedInfoData(data, game)
                .Select(pair => RecordDetailHelpers.Scalar(pair.Label, pair.Value)));
        }

        // Flags held on the model itself (a runtime TESTopicInfo, or a non-FO3/FNV extractor). Shown only when
        // no DATA was parsed, or when they disagree with it, and always labelled as not coming from DATA.
        // (A lifted comparison: a missing DATA byte is null, which never equals a non-zero model byte.)
        byte? dataFlags1 = data?.Flags1;
        byte? dataFlags2 = data?.Flags2;
        if (info.InfoFlags != 0 && dataFlags1 != info.InfoFlags)
        {
            entries.Add(RecordDetailHelpers.Scalar("Flags 1 (not from DATA)",
                FormatFlags(info.InfoFlags, fields?[InfoSerializedData.Flags1Offset])));
        }

        if (info.InfoFlagsExt != 0 && dataFlags2 != info.InfoFlagsExt)
        {
            entries.Add(RecordDetailHelpers.Scalar("Flags 2 (not from DATA)",
                FormatFlags(info.InfoFlagsExt, fields?[InfoSerializedData.Flags2Offset])));
        }

        var speechChallenge = ((dataFlags1 ?? info.InfoFlags) & DialogueRecord.SpeechChallengeFlag) != 0;
        if (info.Difficulty != 0 || speechChallenge)
        {
            entries.Add(RecordDetailHelpers.Scalar("Difficulty",
                $"{info.DifficultyName} ({info.Difficulty.ToString(CultureInfo.InvariantCulture)})"));
        }

        entries.Add(RecordDetailHelpers.Scalar("Prompt", info.PromptText));
        entries.Add(RecordDetailHelpers.Scalar("Info Index",
            info.InfoIndex != 0 ? info.InfoIndex.ToString(CultureInfo.InvariantCulture) : null));
        entries.Add(RecordDetailHelpers.Scalar("Said Once", info.SaidOnce ? "Yes (runtime bSaidOnce)" : null));
        return RecordDetailHelpers.Section("Data", entries);
    }

    private static RecordDetailSection BuildResponses(DialogueRecord info, FormIdResolver resolver)
    {
        var entries = new List<RecordDetailEntry>();
        for (var i = 0; i < info.Responses.Count; i++)
        {
            var response = info.Responses[i];
            var prefix = $"Response {(i + 1).ToString(CultureInfo.InvariantCulture)}";
            entries.Add(RecordDetailHelpers.Scalar($"{prefix} Number",
                response.ResponseNumber.ToString(CultureInfo.InvariantCulture)));
            entries.Add(RecordDetailHelpers.Scalar($"{prefix} Emotion",
                $"{response.EmotionName} ({response.EmotionValue.ToString("+#;-#;0", CultureInfo.InvariantCulture)})"));
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.TextBlock,
                Label = $"{prefix} Text",
                Value = string.IsNullOrEmpty(response.Text) ? "(no text)" : response.Text
            });
            AddFormLink(entries, $"{prefix} Sound", response.SoundFormId, resolver);
        }

        return RecordDetailHelpers.Section("Responses", entries);
    }

    private static RecordDetailSection BuildLinks(DialogueRecord info, FormIdResolver resolver)
    {
        var entries = new List<RecordDetailEntry>();
        AddFormList(entries, "Link To Topics (TCLT)", info.LinkToTopics, resolver);
        AddFormList(entries, "Link From Topics (TCLF)", info.LinkFromTopics, resolver);
        AddFormList(entries, "Add Topics (NAME)", info.AddTopics, resolver);
        AddFormList(entries, "Follow-Up INFOs", info.FollowUpInfos, resolver);
        return RecordDetailHelpers.Section("Links", entries);
    }

    private static RecordDetailSection BuildResultScripts(
        DialogueRecord info,
        FormIdResolver resolver,
        bool isMemoryDumpInput)
    {
        var entries = new List<RecordDetailEntry>();
        var slots = ResolveSlots(info);
        if (slots.Count == 0)
        {
            entries.Add(RecordDetailHelpers.Scalar("Result Scripts", DescribeNoResultScripts(info, isMemoryDumpInput)));
        }

        foreach (var slot in slots)
        {
            AppendSlot(entries, slot, resolver, isMemoryDumpInput);
        }

        return RecordDetailHelpers.Section("Result Scripts", entries);
    }

    /// <summary>
    ///     Pairs each result-script slot with its script. The parser's block list names every slot, empty ones
    ///     included; when it is absent or does not describe <see cref="DialogueRecord.ResultScripts" /> exactly,
    ///     the scripts are listed alone with the labels the dialogue viewer uses.
    /// </summary>
    private static List<ResultScriptSlot> ResolveSlots(DialogueRecord info)
    {
        var blocks = info.ResultScriptBlocks;
        if (blocks.Count > 0 && BlocksDescribeScripts(blocks, info.ResultScripts.Count))
        {
            return blocks
                .Select(block => new ResultScriptSlot(
                    SlotLabel(block.Slot, blocks.Count, block.HasNextSeparator),
                    block.ResultScriptIndex is { } index ? info.ResultScripts[index] : null,
                    block))
                .ToList();
        }

        return info.ResultScripts
            .Select((script, index) => new ResultScriptSlot(
                SlotLabel(index, info.ResultScripts.Count, script.HasNextSeparator),
                script,
                null))
            .ToList();
    }

    private static bool BlocksDescribeScripts(IReadOnlyList<InfoResultScriptBlock> blocks, int scriptCount)
    {
        var expected = 0;
        foreach (var block in blocks)
        {
            if (block.ResultScriptIndex is not { } index)
            {
                continue;
            }

            if (index != expected)
            {
                return false;
            }

            expected++;
        }

        return expected == scriptCount;
    }

    /// <summary>
    ///     The slot heading, following the dialogue viewer: a lone block with no NEXT is just "Result Script";
    ///     otherwise the first block is Begin and the second End. A third block has no GECK slot and says so.
    /// </summary>
    private static string SlotLabel(int index, int count, bool hasNextSeparator)
    {
        if (count == 1 && !hasNextSeparator)
        {
            return "Result Script";
        }

        return index switch
        {
            0 => "Result Script (Begin)",
            1 => "Result Script (End)",
            _ => $"Result Script (block {(index + 1).ToString(CultureInfo.InvariantCulture)}, beyond Begin/End)"
        };
    }

    private static void AppendSlot(
        List<RecordDetailEntry> entries,
        ResultScriptSlot slot,
        FormIdResolver resolver,
        bool isMemoryDumpInput)
    {
        var label = slot.Label;
        if (slot.Script is not { } script)
        {
            entries.Add(RecordDetailHelpers.Scalar(label, DescribeEmptyBlock(slot.Block)));
            return;
        }

        var classification = ScriptSourceProvenance.Classify(script, isMemoryDumpInput);
        foreach (var binding in script.ExternalVariableBindings)
            entries.Add(RecordDetailHelpers.Scalar($"{label} External Variable", binding.Summary));
        var compiledLength = script.CompiledData?.Length ?? 0;
        entries.Add(RecordDetailHelpers.Scalar(label, DescribeScriptSummary(script, compiledLength)));
        entries.Add(RecordDetailHelpers.Scalar($"{label} Provenance",
            classification.CorrespondenceToken == ScriptSourceProvenance.CorrespondenceNotApplicable
                ? classification.Token
                : $"{classification.Token} (correspondence: {classification.CorrespondenceToken})"));

        if (classification.HasSourceText)
        {
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.CodeBlock,
                Label = $"{label}: {classification.Label}",
                Value = script.SourceText
            });
        }
        else
        {
            entries.Add(RecordDetailHelpers.Scalar($"{label} Source", classification.Label));
        }

        // A reconstructed SCTX already supplies this view, including its declaration block.
        // Do not show a second body under the same reconstruction heading.
        if (!string.IsNullOrEmpty(script.DecompiledText) &&
            !(classification.HasSourceText && classification.Kind == ScriptTextKind.ReconstructedDecompiled))
        {
            entries.Add(new RecordDetailEntry
            {
                Kind = RecordDetailEntryKind.CodeBlock,
                Label = $"{label}: {ScriptSourceProvenance.DecompiledTextLabel}",
                Value = script.DecompiledText
            });
        }

        entries.Add(RecordDetailHelpers.Scalar($"{label} Compiled Size",
            DescribeCompiledSize(compiledLength, slot.Block)));
        entries.Add(RecordDetailHelpers.Scalar($"{label} Bytecode Order",
            compiledLength > 0
                ? $"{(script.IsBigEndianBytecode ? "Big-Endian" : "Little-Endian")} (chosen from the SCDA payload; " +
                  "the deciding rule is not recorded for INFO result scripts)"
                : null));

        if (script.Variables.Count > 0)
        {
            entries.Add(ListEntry($"{label} Variables", script.Variables
                .OrderBy(variable => variable.Index)
                .Select(variable => new RecordDetailListItem
                {
                    Label = $"[{variable.Index.ToString(CultureInfo.InvariantCulture)}]",
                    Value = ScriptVariableTypeResolver.FormatDeclaration(variable, script.ReferencedObjects)
                })
                .ToList()));
        }

        if (script.ReferencedObjects.Count > 0)
        {
            // Numbered from 1: the bytecode addresses the mixed SCRO/SCRV table by 1-based slot.
            entries.Add(ListEntry($"{label} References", script.ReferencedObjects
                .Select((reference, index) => new RecordDetailListItem
                {
                    Label = (index + 1).ToString(CultureInfo.InvariantCulture),
                    Value = GeckScriptWriter.FormatScriptReference(reference, script.Variables, resolver),
                    LinkedFormId = (reference & ScrvLocalMarker) == 0 && reference != 0 ? reference : null
                })
                .ToList()));
        }

        if (script.IsIncompleteExecutableBundle)
        {
            entries.Add(RecordDetailHelpers.Scalar($"{label} Bundle",
                "incomplete: the SCHR/SCDA/local/reference bundle is structurally inconsistent or failed " +
                "emission safety validation; this is not a validated, runnable script"));
        }
    }

    private static string DescribeScriptSummary(DialogueResultScript script, int compiledLength)
    {
        if (compiledLength > 0)
        {
            return $"{compiledLength.ToString(CultureInfo.InvariantCulture)} bytes of compiled code (SCDA)";
        }

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            return $"{NoCompiledCode} (the block holds source text but no SCDA)";
        }

        if (script.WithheldSourceReason is not null)
        {
            return $"{NoCompiledCode}; captured source was withheld by validation";
        }

        return script.Variables.Count > 0 || script.ReferencedObjects.Count > 0
            ? $"{NoCompiledCode} (the block holds locals or references but no SCDA or source text)"
            : $"{NoCompiledCode} (the block holds no SCDA or source text)";
    }

    private static string DescribeEmptyBlock(InfoResultScriptBlock? block)
    {
        return block switch
        {
            { DeclaredCompiledSize: 0 } =>
                $"{NoCompiledCode} (the SCHR header declares 0 compiled bytes; the block has no SCDA, SCTX, " +
                "locals or references)",
            { HasSchrHeader: true } =>
                $"{NoCompiledCode} (SCHR header only; the block has no SCDA, SCTX, locals or references)",
            _ => $"{NoCompiledCode} (the block has no SCDA, SCTX, locals or references)"
        };
    }

    private static string DescribeCompiledSize(int compiledLength, InfoResultScriptBlock? block)
    {
        var text = compiledLength > 0
            ? $"{compiledLength.ToString(CultureInfo.InvariantCulture)} bytes"
            : "0 bytes (no SCDA)";
        return block?.DeclaredCompiledSize is { } declared && declared != (uint)compiledLength
            ? $"{text} (the SCHR header declares {declared.ToString(CultureInfo.InvariantCulture)})"
            : text;
    }

    private static string DescribeNoResultScripts(DialogueRecord info, bool isMemoryDumpInput)
    {
        if (info.HasResultScript)
        {
            return $"{NoCompiledCode}: an SCHR header is present, but no block carried SCDA, SCTX, locals or references";
        }

        return isMemoryDumpInput
            ? $"none recovered: {ScriptSourceProvenance.PartialDumpAbsenceWording}"
            : "none (no SCHR in this record)";
    }

    private static string DescribeDataLength(InfoSerializedData data)
    {
        var length = data.DataLength.ToString(CultureInfo.InvariantCulture);
        if (data.IsTruncated)
        {
            return $"{length} bytes (shorter than the {InfoSerializedData.CompleteLength}-byte schema struct; " +
                   "absent fields are not shown)";
        }

        return data.DataLength > InfoSerializedData.CompleteLength
            ? $"{length} bytes ({(data.DataLength - InfoSerializedData.CompleteLength).ToString(CultureInfo.InvariantCulture)} " +
              "bytes beyond the schema struct are not interpreted)"
            : $"{length} bytes";
    }

    /// <summary>
    ///     The four INFO DATA fields of the game's generated schema (Type, Next Speaker, Flags 1, Flags 2), in
    ///     the order <see cref="InfoSerializedData" /> reads them, or null when the game has no such schema or
    ///     its DATA struct is not four single-byte fields.
    /// </summary>
    private static FieldDef[]? TryGetInfoDataFields(BethesdaGame game)
    {
        if (game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas) ||
            EsmSchemas.IndexForGame(game) is not { } index ||
            !index.TryGetValue("INFO", out var infoDef))
        {
            return null;
        }

        var dataDef = infoDef.Members.OfType<StructDef>().FirstOrDefault(member => member.Signature == "DATA");
        if (dataDef is null || dataDef.Members.Count < InfoSerializedData.CompleteLength)
        {
            return null;
        }

        var fields = new FieldDef[InfoSerializedData.CompleteLength];
        for (var i = 0; i < fields.Length; i++)
        {
            if (dataDef.Members[i] is not FieldDef { Type: PrimType.U8 } field)
            {
                return null;
            }

            fields[i] = field;
        }

        return fields;
    }

    private static string FieldName(FieldDef[]? fields, int offset, string fallback)
    {
        return fields?[offset].Name ?? fallback;
    }

    private static string FormatEnum(byte value, FieldDef? field)
    {
        var raw = value.ToString(CultureInfo.InvariantCulture);
        if (field?.InlineEnum is not { } enumDef)
        {
            return raw;
        }

        var label = enumDef.Members.FirstOrDefault(member => member.Value == value)?.Label;
        return label is null ? $"{raw} (not named in the schema)" : $"{label} ({raw})";
    }

    private static string FormatFlags(byte value, FieldDef? field)
    {
        var hex = $"0x{value:X2}";
        if (value == 0)
        {
            return $"{hex} (none)";
        }

        if (field?.InlineFlags is not { } flagsDef)
        {
            return hex;
        }

        var names = new List<string>(8);
        for (var bit = 0; bit < 8; bit++)
        {
            if ((value & (1 << bit)) == 0)
            {
                continue;
            }

            names.Add(flagsDef.Bits.FirstOrDefault(member => member.Bit == bit)?.Label
                      ?? $"bit {bit.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"{hex} ({string.Join(", ", names)})";
    }

    private static void AddFormLink(List<RecordDetailEntry> entries, string label, uint? formId, FormIdResolver resolver)
    {
        if (formId is not { } id || id == 0)
        {
            return;
        }

        entries.Add(new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.Link,
            Label = label,
            Value = ConditionTextFormatter.FormatFormId(id, resolver.GetEditorId(id)),
            LinkedFormId = id
        });
    }

    private static void AddFormList(
        List<RecordDetailEntry> entries,
        string label,
        IReadOnlyList<uint> formIds,
        FormIdResolver resolver)
    {
        if (formIds.Count == 0)
        {
            return;
        }

        entries.Add(ListEntry(label, formIds
            .Select(formId => new RecordDetailListItem
            {
                Label = ConditionTextFormatter.FormatFormId(formId, resolver.GetEditorId(formId)),
                LinkedFormId = formId
            })
            .ToList()));
    }

    private static RecordDetailEntry ListEntry(string label, List<RecordDetailListItem> items)
    {
        return new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.List,
            Label = label,
            Items = items,
            ExpandByDefault = items.Count <= 8
        };
    }

    /// <summary>One result-script slot: its heading, its script (null for an empty block) and its block, if known.</summary>
    private readonly record struct ResultScriptSlot(
        string Label,
        DialogueResultScript? Script,
        InfoResultScriptBlock? Block);
}
