using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Parses INFO records from ESM data, handling CTDA conditions, subrecord iteration,
///     and split INFO record merging (Xbox 360 splits INFO into Base + Response records).
/// </summary>
internal sealed class DialogueConditionParser(RecordParserContext context) : RecordHandlerBase(context)
{

    /// <summary>
    ///     Parse all INFO records into DialogueRecord instances.
    ///     Uses accessor-based subrecord parsing when available, otherwise falls back to scan result.
    /// </summary>
    internal List<DialogueRecord> ParseAllInfoRecords()
    {
        var dialogues = new List<DialogueRecord>();
        var infoRecords = Context.GetRecordsByType("INFO").ToList();
        var log = Logger.Instance;

        if (Context.Accessor == null)
        {
            foreach (var record in infoRecords)
            {
                var dialogue = ParseDialogueFromScanResult(record);
                if (dialogue != null)
                {
                    dialogues.Add(dialogue);
                }
            }

            log.Info("  [Dialogue] ParseAllInfoRecords: {0} INFO records (scan-only path, no accessor)",
                infoRecords.Count);
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in infoRecords)
                {
                    var dialogue = ParseDialogueFromAccessor(record, buffer);
                    if (dialogue != null)
                    {
                        dialogues.Add(dialogue);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return dialogues;
    }

    /// <summary>
    ///     Merges split INFO records that share the same FormID.
    ///     Xbox 360 splits INFO into Base (CTDA, ANAM, PNAM, TCLT, TCLF, NAME, DNAM, QSTI)
    ///     and Response (TRDT, NAM1) records. This combines them into a single DialogueRecord.
    /// </summary>
    internal static List<DialogueRecord> MergeSplitInfoRecords(List<DialogueRecord> dialogues)
    {
        return dialogues
            .GroupBy(d => d.FormId)
            .Select(g =>
            {
                // Start with the record that has the most response data
                var best = g.OrderByDescending(d => d.Responses.Count)
                    .ThenByDescending(d => d.Responses.Sum(r => r.Text?.Length ?? 0))
                    .First();

                // Merge missing fields from other records with the same FormID
                foreach (var other in g)
                {
                    if (ReferenceEquals(other, best))
                    {
                        continue;
                    }

                    if (best.SpeakerFormId is null or 0 && other.SpeakerFormId is not null and not 0)
                    {
                        best = best with { SpeakerFormId = other.SpeakerFormId };
                    }

                    if (best.SpeakerFactionFormId is null or 0 && other.SpeakerFactionFormId is not null and not 0)
                    {
                        best = best with { SpeakerFactionFormId = other.SpeakerFactionFormId };
                    }

                    if (best.SpeakerRaceFormId is null or 0 && other.SpeakerRaceFormId is not null and not 0)
                    {
                        best = best with { SpeakerRaceFormId = other.SpeakerRaceFormId };
                    }

                    if (best.SpeakerVoiceTypeFormId is null or 0 && other.SpeakerVoiceTypeFormId is not null and not 0)
                    {
                        best = best with { SpeakerVoiceTypeFormId = other.SpeakerVoiceTypeFormId };
                    }

                    if (best.SpeakerAnimationFormId is null or 0 && other.SpeakerAnimationFormId is not null and not 0)
                    {
                        best = best with { SpeakerAnimationFormId = other.SpeakerAnimationFormId };
                    }

                    if (best.QuestFormId is null or 0 && other.QuestFormId is not null and not 0)
                    {
                        best = best with { QuestFormId = other.QuestFormId };
                    }

                    if (best.TopicFormId is null or 0 && other.TopicFormId is not null and not 0)
                    {
                        best = best with { TopicFormId = other.TopicFormId };
                    }

                    if (best.PreviousInfo is null or 0 && other.PreviousInfo is not null and not 0)
                    {
                        best = best with { PreviousInfo = other.PreviousInfo };
                    }

                    if (best.Difficulty == 0 && other.Difficulty != 0)
                    {
                        best = best with { Difficulty = other.Difficulty };
                    }

                    if (string.IsNullOrEmpty(best.EditorId) && !string.IsNullOrEmpty(other.EditorId))
                    {
                        best = best with { EditorId = other.EditorId };
                    }

                    if (best.LinkToTopics.Count == 0 && other.LinkToTopics.Count > 0)
                    {
                        best = best with { LinkToTopics = other.LinkToTopics };
                    }

                    if (best.LinkFromTopics.Count == 0 && other.LinkFromTopics.Count > 0)
                    {
                        best = best with { LinkFromTopics = other.LinkFromTopics };
                    }

                    if (best.AddTopics.Count == 0 && other.AddTopics.Count > 0)
                    {
                        best = best with { AddTopics = other.AddTopics };
                    }

                    if (best.Responses.Count == 0 && other.Responses.Count > 0)
                    {
                        best = best with { Responses = other.Responses };
                    }

                    if (other.ConditionFunctions.Count > 0)
                    {
                        var merged = new HashSet<ushort>(best.ConditionFunctions);
                        merged.UnionWith(other.ConditionFunctions);
                        best = best with { ConditionFunctions = merged.ToList() };
                    }

                    if (other.Conditions.Count > 0)
                    {
                        best = best with
                        {
                            Conditions = best.Conditions
                                .Concat(other.Conditions)
                                .Distinct()
                                .ToList()
                        };
                    }

                    if (other.ResultScripts.Count > 0)
                    {
                        best = best with
                        {
                            ResultScripts = MergeResultScripts(best.ResultScripts, other.ResultScripts),
                            HasResultScript = best.HasResultScript || other.HasResultScript
                        };
                    }
                }

                var rawRecordOffset = g
                    .Select(dialogue => dialogue.RawRecordOffset)
                    .Where(offset => offset > 0)
                    .DefaultIfEmpty(best.RawRecordOffset)
                    .Min();
                var runtimeStructOffset = g
                    .Select(dialogue => dialogue.RuntimeStructOffset)
                    .FirstOrDefault(offset => offset > 0);
                var tesFileOffset = g
                    .Select(dialogue => dialogue.TesFileOffset)
                    .FirstOrDefault(offset => offset > 0);

                best = best with
                {
                    RawRecordOffset = rawRecordOffset > 0 ? rawRecordOffset : best.RawRecordOffset,
                    RuntimeStructOffset = best.RuntimeStructOffset > 0
                        ? best.RuntimeStructOffset
                        : runtimeStructOffset,
                    TesFileOffset = best.TesFileOffset != 0
                        ? best.TesFileOffset
                        : tesFileOffset
                };

                return best;
            })
            .ToList();
    }

    private DialogueRecord? ParseDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find response texts strictly within this INFO record's data bounds
        var dataStart = record.Offset + 24; // Skip main record header
        var dataEnd = dataStart + record.DataSize;
        var responseTexts = Context.ScanResult.ResponseTexts
            .Where(r => r.Offset >= dataStart && r.Offset < dataEnd)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            RawRecordOffset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private DialogueRecord? ParseDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseDialogueFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint? topicFormId = null;
        uint? questFormId = null;
        uint? speakerFormId = null;
        uint? previousInfo = null;
        uint difficulty = 0;
        var hasResultScript = false;
        var responses = new List<DialogueResponse>();
        var linkToTopics = new List<uint>();
        var linkFromTopics = new List<uint>();
        var addTopics = new List<uint>();

        // CTDA condition-based speaker tracking
        uint? conditionSpeaker = null;
        uint? conditionFaction = null;
        uint? conditionRace = null;
        uint? conditionVoiceType = null;
        uint? speakerAnimationFormId = null;
        var conditionFunctions = new List<ushort>();
        var conditions = new List<DialogueCondition>();

        // Result scripts
        var resultSourceTexts = new List<string>();
        var resultScriptBlocks = new List<DialogueResultScriptBuilder>();
        DialogueResultScriptBuilder? currentResultScript = null;
        uint? pendingVariableIndex = null;
        byte pendingVariableType = 0;

        // Track current response being built
        string? currentResponseText = null;
        uint currentEmotionType = 0;
        var currentEmotionValue = 0;
        byte currentResponseNumber = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    break;
                case "QSTI" when sub.DataLength == 4:
                    questFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM1":
                    // Save previous response if any
                    if (currentResponseText != null)
                    {
                        responses.Add(new DialogueResponse
                        {
                            Text = currentResponseText,
                            EmotionType = currentEmotionType,
                            EmotionValue = currentEmotionValue,
                            ResponseNumber = currentResponseNumber
                        });
                    }

                    currentResponseText = EsmStringUtils.ReadNullTermString(subData);
                    currentEmotionType = 0;
                    currentEmotionValue = 0;
                    break;
                case "TRDT" when sub.DataLength >= 24:
                {
                    var fields = SubrecordDataReader.ReadFields("TRDT", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        currentEmotionType = SubrecordDataReader.GetUInt32(fields, "EmotionType");
                        currentEmotionValue = SubrecordDataReader.GetInt32(fields, "EmotionValue");
                        currentResponseNumber = SubrecordDataReader.GetByte(fields, "ResponseNumber");
                    }
                }

                    break;
                case "PNAM" when sub.DataLength == 4:
                    previousInfo = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ANAM" when sub.DataLength == 4:
                    speakerFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 4:
                    // SNAM = Speaker Animation (IDLE FormID), not the speaker NPC.
                    speakerAnimationFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPIC" when sub.DataLength == 4:
                    topicFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TCLT" when sub.DataLength == 4:
                {
                    var tcltFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (tcltFormId != 0)
                    {
                        linkToTopics.Add(tcltFormId);
                    }

                    break;
                }
                case "TCLF" when sub.DataLength == 4:
                {
                    var tclfFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (tclfFormId != 0)
                    {
                        linkFromTopics.Add(tclfFormId);
                    }

                    break;
                }
                case "NAME" when sub.DataLength == 4:
                {
                    var nameFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (nameFormId != 0)
                    {
                        addTopics.Add(nameFormId);
                    }

                    break;
                }
                case "DNAM" when sub.DataLength >= 4:
                    difficulty = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (difficulty > 10)
                    {
                        difficulty = 0;
                    }

                    break;
                case "SCHR":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    hasResultScript = true;
                    currentResultScript = new DialogueResultScriptBuilder();
                    resultScriptBlocks.Add(currentResultScript);
                    break;
                case "SCTX":
                {
                    var sourceText = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(sourceText))
                    {
                        resultSourceTexts.Add(sourceText);
                    }

                    break;
                }
                case "SCDA":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.CompiledData = subData.ToArray();
                    break;
                case "SCRO" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.ReferencedObjects.Add(
                        RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
                case "SLSD" when sub.DataLength >= 16:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    pendingVariableIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    var isIntegerRaw = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    pendingVariableType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;
                case "SCVR":
                {
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableName = EsmStringUtils.ReadNullTermString(subData);
                    if (pendingVariableIndex.HasValue)
                    {
                        currentResultScript.Variables.Add(new ScriptVariableInfo(
                            pendingVariableIndex.Value, variableName, pendingVariableType));
                        pendingVariableIndex = null;
                    }

                    break;
                }
                case "SCRV" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableIndex = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    currentResultScript.ReferencedObjects.Add(0x80000000 | variableIndex);
                    break;
                case "NEXT":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.HasNextSeparator = true;
                    currentResultScript = null;
                    break;
                case "CTDA" when sub.DataLength >= 28:
                {
                    var condition = ParseCtdaCondition(subData, record.IsBigEndian, conditionFunctions,
                        ref conditionSpeaker, ref conditionFaction, ref conditionRace, ref conditionVoiceType);
                    if (condition != null)
                    {
                        conditions.Add(condition);
                    }

                    break;
                }
            }
        }

        FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);

        // Add final response if any
        if (currentResponseText != null)
        {
            responses.Add(new DialogueResponse
            {
                Text = currentResponseText,
                EmotionType = currentEmotionType,
                EmotionValue = currentEmotionValue,
                ResponseNumber = currentResponseNumber
            });
        }

        var resultScripts = BuildResultScripts(
            resultSourceTexts,
            resultScriptBlocks,
            editorId ?? Context.GetEditorId(record.FormId),
            record.FormId);
        if (resultScripts.Count > 0)
        {
            hasResultScript = true;
        }

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            // Speaker priority: ANAM > CTDA GetIsID > (topic TNAM propagated later)
            // Note: SNAM is Speaker *Animation* (IDLE FormID), NOT a speaker NPC.
            SpeakerFormId = speakerFormId ?? conditionSpeaker,
            SpeakerFactionFormId = conditionFaction,
            SpeakerRaceFormId = conditionRace,
            SpeakerVoiceTypeFormId = conditionVoiceType,
            SpeakerAnimationFormId = speakerAnimationFormId,
            ConditionFunctions = conditionFunctions,
            Conditions = conditions,
            Responses = responses,
            PreviousInfo = previousInfo,
            Difficulty = difficulty,
            LinkToTopics = linkToTopics,
            LinkFromTopics = linkFromTopics,
            AddTopics = addTopics,
            HasResultScript = hasResultScript,
            ResultScripts = resultScripts,
            Offset = record.Offset,
            RawRecordOffset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse a CTDA condition subrecord, extracting speaker-related function parameters.
    /// </summary>
    private static DialogueCondition? ParseCtdaCondition(
        Span<byte> subData,
        bool isBigEndian,
        List<ushort> conditionFunctions,
        ref uint? conditionSpeaker,
        ref uint? conditionFaction,
        ref uint? conditionRace,
        ref uint? conditionVoiceType)
    {
        var fields = SubrecordDataReader.ReadFields("CTDA", null, subData, isBigEndian);
        if (fields.Count == 0)
        {
            return null;
        }

        var functionIndex = SubrecordDataReader.GetUInt16(fields, "FunctionIndex");
        conditionFunctions.Add(functionIndex);

        var param1 = SubrecordDataReader.GetUInt32(fields, "Parameter1");
        var runOn = SubrecordDataReader.GetUInt32(fields, "RunOn");
        var reference = SubrecordDataReader.GetUInt32(fields, "Reference");
        var compValue = SubrecordDataReader.GetFloat(fields, "ComparisonValue");
        var typeByte = SubrecordDataReader.GetByte(fields, "Type");
        var compOp = (typeByte >> 5) & 0x7; // 0=Eq, 1=NotEq, 2=Gt, 3=GtEq, 4=Lt, 5=LtEq

        // Positive match: boolean function returns true for this param
        // Equal/GtEq + compValue~1.0  OR  NotEqual + compValue~0.0  OR  Greater + compValue~0.0
        var isPositive = runOn == 0 &&
                         ((compOp is 0 or 3 && compValue >= 0.99f) ||
                          (compOp is 1 && compValue < 0.01f) ||
                          (compOp is 2 && compValue < 0.01f));

        if (isPositive)
        {
            switch (functionIndex)
            {
                case 0x48: // GetIsID -> specific NPC speaker
                    conditionSpeaker ??= param1;
                    break;
                case 0x47: // GetInFaction -> faction-based shared dialogue
                    conditionFaction ??= param1;
                    break;
                case 0x45: // GetIsRace -> race-based dialogue
                    conditionRace ??= param1;
                    break;
                case 0x1AB: // GetIsVoiceType -> voice-type-based generic dialogue
                    conditionVoiceType ??= param1;
                    break;
            }
        }

        return new DialogueCondition
        {
            Type = typeByte,
            ComparisonValue = compValue,
            FunctionIndex = functionIndex,
            Parameter1 = param1,
            Parameter2 = SubrecordDataReader.GetUInt32(fields, "Parameter2"),
            RunOn = runOn,
            Reference = reference
        };
    }

    private List<DialogueResultScript> BuildResultScripts(
        List<string> sourceTexts,
        List<DialogueResultScriptBuilder> blocks,
        string? editorId,
        uint infoFormId)
    {
        if (blocks.Count == 0)
        {
            return sourceTexts
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new DialogueResultScript { SourceText = text })
                .ToList();
        }

        AssignSourceTextsToBlocks(sourceTexts, blocks);

        var resultScripts =
            new List<DialogueResultScript>(blocks.Count + Math.Max(0, sourceTexts.Count - blocks.Count));
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var decompiledText = TryDecompileResultScript(block, editorId, infoFormId, i);
            resultScripts.Add(new DialogueResultScript
            {
                SourceText = block.SourceText,
                DecompiledText = decompiledText,
                CompiledData = block.CompiledData,
                ReferencedObjects = block.ReferencedObjects
                    .Where(formId => (formId & 0x80000000) == 0)
                    .ToList(),
                HasNextSeparator = block.HasNextSeparator
            });
        }

        if (sourceTexts.Count > blocks.Count)
        {
            for (var i = blocks.Count; i < sourceTexts.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(sourceTexts[i]))
                {
                    resultScripts.Add(new DialogueResultScript { SourceText = sourceTexts[i] });
                }
            }
        }

        return resultScripts
            .Where(script => script.HasContent)
            .ToList();
    }

    private static void AssignSourceTextsToBlocks(List<string> sourceTexts, List<DialogueResultScriptBuilder> blocks)
    {
        if (sourceTexts.Count == 0 || blocks.Count == 0)
        {
            return;
        }

        if (sourceTexts.Count >= blocks.Count)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                blocks[i].SourceText ??= sourceTexts[i];
            }

            return;
        }

        var sourceIndex = 0;
        for (var i = 0; i < blocks.Count && sourceIndex < sourceTexts.Count; i++)
        {
            if (blocks[i].CompiledData is { Length: > 0 })
            {
                blocks[i].SourceText ??= sourceTexts[sourceIndex++];
            }
        }

        for (var i = 0; i < blocks.Count && sourceIndex < sourceTexts.Count; i++)
        {
            if (string.IsNullOrEmpty(blocks[i].SourceText))
            {
                blocks[i].SourceText = sourceTexts[sourceIndex++];
            }
        }
    }

    private string? TryDecompileResultScript(
        DialogueResultScriptBuilder block,
        string? editorId,
        uint infoFormId,
        int index)
    {
        if (block.CompiledData is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var scriptName = !string.IsNullOrWhiteSpace(editorId)
                ? $"{editorId}_Result_{index + 1}"
                : $"INFO_{infoFormId:X8}_Result_{index + 1}";
            var decompiler = new ScriptDecompiler(
                block.Variables,
                block.ReferencedObjects,
                Context.ResolveFormName,
                false,
                scriptName);
            return decompiler.Decompile(block.CompiledData);
        }
        catch (Exception ex)
        {
            return $"; Decompilation failed: {ex.Message}";
        }
    }

    private static DialogueResultScriptBuilder StartImplicitResultScript(List<DialogueResultScriptBuilder> blocks)
    {
        var block = new DialogueResultScriptBuilder();
        blocks.Add(block);
        return block;
    }

    private static void FlushPendingVariable(
        DialogueResultScriptBuilder? currentResultScript,
        ref uint? pendingVariableIndex,
        ref byte pendingVariableType)
    {
        if (!pendingVariableIndex.HasValue || currentResultScript == null)
        {
            return;
        }

        currentResultScript.Variables.Add(new ScriptVariableInfo(
            pendingVariableIndex.Value, null, pendingVariableType));
        pendingVariableIndex = null;
        pendingVariableType = 0;
    }

    private static List<DialogueResultScript> MergeResultScripts(
        List<DialogueResultScript> primary,
        List<DialogueResultScript> secondary)
    {
        if (primary.Count == 0)
        {
            return secondary;
        }

        if (secondary.Count == 0)
        {
            return primary;
        }

        var maxCount = Math.Max(primary.Count, secondary.Count);
        var merged = new List<DialogueResultScript>(maxCount);

        for (var i = 0; i < maxCount; i++)
        {
            var left = i < primary.Count ? primary[i] : null;
            var right = i < secondary.Count ? secondary[i] : null;

            if (left == null)
            {
                merged.Add(right!);
                continue;
            }

            if (right == null)
            {
                merged.Add(left);
                continue;
            }

            merged.Add(new DialogueResultScript
            {
                SourceText = left.SourceText ?? right.SourceText,
                DecompiledText = left.DecompiledText ?? right.DecompiledText,
                CompiledData = left.CompiledData ?? right.CompiledData,
                ReferencedObjects = left.ReferencedObjects
                    .Concat(right.ReferencedObjects)
                    .Distinct()
                    .ToList(),
                HasNextSeparator = left.HasNextSeparator || right.HasNextSeparator
            });
        }

        return merged
            .Where(script => script.HasContent)
            .ToList();
    }

    /// <summary>
    ///     Parse result scripts (SCHR/SCTX/SCDA/SCRO/SLSD/SCVR/SCRV/NEXT) from raw ESM subrecord data.
    ///     Used by the DMP path to extract result scripts from memory-mapped ESM pages.
    /// </summary>
    internal static List<DialogueResultScript> ParseResultScriptsFromSubrecords(
        byte[] data, int dataSize, bool isBigEndian,
        string? editorId, uint formId,
        Func<uint, string?>? resolveFormName = null)
    {
        var resultSourceTexts = new List<string>();
        var resultScriptBlocks = new List<DialogueResultScriptBuilder>();
        DialogueResultScriptBuilder? currentResultScript = null;
        uint? pendingVariableIndex = null;
        byte pendingVariableType = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, isBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "SCHR":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    currentResultScript = new DialogueResultScriptBuilder();
                    resultScriptBlocks.Add(currentResultScript);
                    break;
                case "SCTX":
                {
                    var sourceText = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(sourceText))
                    {
                        resultSourceTexts.Add(sourceText);
                    }

                    break;
                }
                case "SCDA":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.CompiledData = subData.ToArray();
                    break;
                case "SCRO" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.ReferencedObjects.Add(
                        RecordParserContext.ReadFormId(subData, isBigEndian));
                    break;
                case "SLSD" when sub.DataLength >= 16:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    pendingVariableIndex = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    var isIntegerRaw = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    pendingVariableType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;
                case "SCVR":
                {
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableName = EsmStringUtils.ReadNullTermString(subData);
                    if (pendingVariableIndex.HasValue)
                    {
                        currentResultScript.Variables.Add(new ScriptVariableInfo(
                            pendingVariableIndex.Value, variableName, pendingVariableType));
                        pendingVariableIndex = null;
                    }

                    break;
                }
                case "SCRV" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableIndex = RecordParserContext.ReadFormId(subData, isBigEndian);
                    currentResultScript.ReferencedObjects.Add(0x80000000 | variableIndex);
                    break;
                case "NEXT":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.HasNextSeparator = true;
                    currentResultScript = null;
                    break;
            }
        }

        FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);

        return BuildResultScriptsStatic(resultSourceTexts, resultScriptBlocks, editorId, formId, resolveFormName);
    }

    /// <summary>
    ///     Static version of BuildResultScripts that accepts an optional form name resolver.
    /// </summary>
    private static List<DialogueResultScript> BuildResultScriptsStatic(
        List<string> sourceTexts,
        List<DialogueResultScriptBuilder> blocks,
        string? editorId,
        uint infoFormId,
        Func<uint, string?>? resolveFormName)
    {
        if (blocks.Count == 0)
        {
            return sourceTexts
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new DialogueResultScript { SourceText = text })
                .ToList();
        }

        AssignSourceTextsToBlocks(sourceTexts, blocks);

        var resultScripts =
            new List<DialogueResultScript>(blocks.Count + Math.Max(0, sourceTexts.Count - blocks.Count));
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var decompiledText = TryDecompileResultScriptStatic(block, editorId, infoFormId, i, resolveFormName);
            resultScripts.Add(new DialogueResultScript
            {
                SourceText = block.SourceText,
                DecompiledText = decompiledText,
                CompiledData = block.CompiledData,
                ReferencedObjects = block.ReferencedObjects
                    .Where(fid => (fid & 0x80000000) == 0)
                    .ToList(),
                HasNextSeparator = block.HasNextSeparator
            });
        }

        if (sourceTexts.Count > blocks.Count)
        {
            for (var i = blocks.Count; i < sourceTexts.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(sourceTexts[i]))
                {
                    resultScripts.Add(new DialogueResultScript { SourceText = sourceTexts[i] });
                }
            }
        }

        return resultScripts
            .Where(script => script.HasContent)
            .ToList();
    }

    private static string? TryDecompileResultScriptStatic(
        DialogueResultScriptBuilder block,
        string? editorId,
        uint infoFormId,
        int index,
        Func<uint, string?>? resolveFormName)
    {
        if (block.CompiledData is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var scriptName = !string.IsNullOrWhiteSpace(editorId)
                ? $"{editorId}_Result_{index + 1}"
                : $"INFO_{infoFormId:X8}_Result_{index + 1}";
            var decompiler = new ScriptDecompiler(
                block.Variables,
                block.ReferencedObjects,
                resolveFormName ?? (formId => $"0x{formId:X8}"),
                false,
                scriptName);
            return decompiler.Decompile(block.CompiledData);
        }
        catch (Exception ex)
        {
            return $"; Decompilation failed: {ex.Message}";
        }
    }

    internal sealed class DialogueResultScriptBuilder
    {
        public string? SourceText { get; set; }
        public byte[]? CompiledData { get; set; }
        public List<uint> ReferencedObjects { get; } = [];
        public List<ScriptVariableInfo> Variables { get; } = [];
        public bool HasNextSeparator { get; set; }
    }
}
