using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class DialogueRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region ReconstructDialogue

    /// <summary>
    ///     Reconstruct all Dialogue (INFO) records from the scan result.
    /// </summary>
    internal List<DialogueRecord> ReconstructDialogue()
    {
        var dialogues = new List<DialogueRecord>();
        var infoRecords = _context.GetRecordsByType("INFO").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in infoRecords)
            {
                var dialogue = ReconstructDialogueFromScanResult(record);
                if (dialogue != null)
                {
                    dialogues.Add(dialogue);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in infoRecords)
                {
                    var dialogue = ReconstructDialogueFromAccessor(record, buffer);
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

        // Merge split INFO records — Xbox 360 splits INFO into Base (conditions, links, speaker)
        // + Response (text, emotion) records with the same FormID. Also handles BE/LE duplicates.
        return MergeSplitInfoRecords(dialogues);
    }

    private DialogueRecord? ReconstructDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find response texts strictly within this INFO record's data bounds
        var dataStart = record.Offset + 24; // Skip main record header
        var dataEnd = dataStart + record.DataSize;
        var responseTexts = _context.ScanResult.ResponseTexts
            .Where(r => r.Offset >= dataStart && r.Offset < dataEnd)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private DialogueRecord? ReconstructDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructDialogueFromScanResult(record);
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
        uint? snamSpeaker = null;
        var conditionFunctions = new List<ushort>();

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
                        _context.FormIdToEditorId[record.FormId] = editorId;
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
                    snamSpeaker = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                    hasResultScript = true;
                    break;
                case "CTDA" when sub.DataLength >= 28:
                {
                    var fields = SubrecordDataReader.ReadFields("CTDA", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var functionIndex = SubrecordDataReader.GetUInt16(fields, "FunctionIndex");
                        conditionFunctions.Add(functionIndex);

                        var param1 = SubrecordDataReader.GetUInt32(fields, "Parameter1");
                        var runOn = SubrecordDataReader.GetUInt32(fields, "RunOn");
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
                    }

                    break;
                }
            }
        }

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

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            // Speaker priority: ANAM > SNAM > CTDA GetIsID > (topic TNAM propagated later)
            SpeakerFormId = speakerFormId ?? snamSpeaker ?? conditionSpeaker,
            SpeakerFactionFormId = conditionFaction,
            SpeakerRaceFormId = conditionRace,
            SpeakerVoiceTypeFormId = conditionVoiceType,
            ConditionFunctions = conditionFunctions,
            Responses = responses,
            PreviousInfo = previousInfo,
            Difficulty = difficulty,
            LinkToTopics = linkToTopics,
            LinkFromTopics = linkFromTopics,
            AddTopics = addTopics,
            HasResultScript = hasResultScript,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Merges split INFO records that share the same FormID.
    ///     Xbox 360 splits INFO into Base (CTDA, ANAM, PNAM, TCLT, TCLF, NAME, DNAM, QSTI)
    ///     and Response (TRDT, NAM1) records. This combines them into a single DialogueRecord.
    /// </summary>
    private static List<DialogueRecord> MergeSplitInfoRecords(List<DialogueRecord> dialogues)
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
                }

                return best;
            })
            .ToList();
    }

    #endregion

    #region ReconstructDialogTopics

    /// <summary>
    ///     Reconstruct all Dialog Topic records from the scan result.
    /// </summary>
    internal List<DialogTopicRecord> ReconstructDialogTopics()
    {
        var topics = new List<DialogTopicRecord>();
        var topicRecords = _context.GetRecordsByType("DIAL").ToList();

        if (_context.Accessor != null)
        {
            // Single-pass subrecord parsing for FULL, TNAM, QSTI, and DATA
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in topicRecords)
                {
                    var recordData = _context.ReadRecordData(record, buffer);
                    if (recordData == null)
                    {
                        // Fallback for unreadable records
                        topics.Add(new DialogTopicRecord
                        {
                            FormId = record.FormId,
                            EditorId = _context.GetEditorId(record.FormId),
                            Offset = record.Offset,
                            IsBigEndian = record.IsBigEndian
                        });
                        continue;
                    }

                    var (data, dataSize) = recordData.Value;

                    string? fullName = null;
                    uint? speakerFormId = null;
                    uint? questFormId = null;
                    byte topicType = 0;
                    byte topicFlags = 0;

                    foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                    {
                        var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
                        switch (sub.Signature)
                        {
                            case "EDID":
                            {
                                var edid = EsmStringUtils.ReadNullTermString(subData);
                                if (!string.IsNullOrEmpty(edid))
                                    _context.FormIdToEditorId[record.FormId] = edid;
                                break;
                            }
                            case "FULL":
                                fullName = EsmStringUtils.ReadNullTermString(subData);
                                break;
                            case "TNAM" when sub.DataLength == 4:
                                speakerFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                                break;
                            case "QSTI" when sub.DataLength == 4:
                                questFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                                break;
                            case "DATA" when sub.DataLength >= 2:
                                // Topic type and flags — raw bytes, no endian swap needed
                                topicType = subData[0];
                                topicFlags = subData[1];
                                break;
                        }
                    }

                    topics.Add(new DialogTopicRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = fullName,
                        SpeakerFormId = speakerFormId,
                        QuestFormId = questFormId,
                        TopicType = topicType,
                        Flags = topicFlags,
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            foreach (var record in topicRecords)
            {
                // Fallback: only accept FULL subrecords strictly within the DIAL record's data
                var fullName = _context.FindFullNameInRecordBounds(record);
                topics.Add(new DialogTopicRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }

        // Deduplicate by FormID - same DIAL record can appear in both BE and LE memory regions.
        // Keep the version with the most data (prefer one with FullName, SpeakerFormId, and EditorId).
        var deduped = topics
            .GroupBy(t => t.FormId)
            .Select(g => g
                .OrderByDescending(t => t.SpeakerFormId.HasValue ? 1 : 0)
                .ThenByDescending(t => t.FullName?.Length ?? 0)
                .ThenByDescending(t => t.EditorId?.Length ?? 0)
                .First())
            .ToList();

        // Probe DIAL runtime struct layout and merge runtime data if reader available
        if (_context.RuntimeReader != null)
        {
            MergeRuntimeDialogTopicData(deduped);
        }

        return deduped;
    }

    /// <summary>
    ///     Detect DIAL FormType from RuntimeEditorIds by cross-referencing known ESM DIAL FormIDs,
    ///     then merge runtime TESTopic struct data (type, flags, priority) into topic records.
    /// </summary>
    private void MergeRuntimeDialogTopicData(List<DialogTopicRecord> topics)
    {
        // Build set of known DIAL FormIDs from ESM scan
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));

        // Detect DIAL FormType by finding RuntimeEditorId entries matching known DIAL FormIDs
        byte? dialFormType = null;
        var formTypeCounts = new Dictionary<byte, int>();
        foreach (var formType in _context.ScanResult.RuntimeEditorIds.Where(entry => knownDialFormIds.Contains(entry.FormId))
                     .Select(entry => entry.FormType))
        {
            formTypeCounts.TryGetValue(formType, out var count);
            formTypeCounts[formType] = count + 1;
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                dialFormType = best.Key;
            }
        }

        if (!dialFormType.HasValue)
        {
            // Fallback: try FormType 0x45 (empirically verified as DIAL+INFO shared FormType).
            // Validate by attempting ReadRuntimeDialogTopic on a few candidate entries.
            const byte candidateFormType = 0x45;
            var validCount = 0;
            var testedCount = 0;
            foreach (var entry in _context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != candidateFormType || !entry.TesFormOffset.HasValue)
                {
                    continue;
                }

                if (++testedCount > 20)
                {
                    break;
                }

                var probe = _context.RuntimeReader!.ReadRuntimeDialogTopic(entry);
                if (probe != null)
                {
                    validCount++;
                }
            }

            if (validCount >= 3)
            {
                dialFormType = candidateFormType;
                Logger.Instance.Debug(
                    $"  [Semantic] DIAL FormType fallback: 0x{candidateFormType:X2} " +
                    $"({validCount}/{testedCount} passed ReadRuntimeDialogTopic validation)");
            }
        }

        if (!dialFormType.HasValue)
        {
            Logger.Instance.Debug("  [Semantic] Could not detect DIAL FormType - no runtime topic data");
            return;
        }

        Logger.Instance.Debug($"  [Semantic] Detected DIAL FormType: 0x{dialFormType.Value:X2} " +
                              $"({formTypeCounts.GetValueOrDefault(dialFormType.Value)} matches " +
                              $"from {knownDialFormIds.Count} known DIALs)");

        // Build FormID -> topic index for merging
        var topicByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < topics.Count; i++)
        {
            topicByFormId.TryAdd(topics[i].FormId, i);
        }

        var mergedCount = 0;
        var newCount = 0;

        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var runtimeTopic = _context.RuntimeReader!.ReadRuntimeDialogTopic(entry);
            if (runtimeTopic == null)
            {
                continue;
            }

            if (topicByFormId.TryGetValue(entry.FormId, out var idx))
            {
                // Merge runtime data into existing ESM topic
                var existing = topics[idx];
                topics[idx] = existing with
                {
                    EditorId = existing.EditorId ?? entry.EditorId,
                    FullName = existing.FullName ?? runtimeTopic.FullName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt
                };
                mergedCount++;
            }
            else
            {
                // New topic from runtime only
                topics.Add(new DialogTopicRecord
                {
                    FormId = entry.FormId,
                    EditorId = entry.EditorId,
                    FullName = runtimeTopic.FullName ?? entry.DisplayName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt,
                    Offset = entry.TesFormOffset.Value,
                    IsBigEndian = true
                });
                newCount++;
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime DIAL merge: {mergedCount} merged, {newCount} new " +
            $"(total: {topics.Count})");
    }

    #endregion

    #region ReconstructQuests

    /// <summary>
    ///     Reconstruct all Quest records from the scan result.
    /// </summary>
    internal List<QuestRecord> ReconstructQuests()
    {
        var quests = new List<QuestRecord>();
        var questRecords = _context.GetRecordsByType("QUST").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in questRecords)
            {
                var quest = ReconstructQuestFromScanResult(record);
                if (quest != null)
                {
                    quests.Add(quest);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32768); // Quests can be larger
            try
            {
                foreach (var record in questRecords)
                {
                    var quest = ReconstructQuestFromAccessor(record, buffer);
                    if (quest != null)
                    {
                        quests.Add(quest);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge quests from runtime struct reading
        if (_context.RuntimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(quests.Select(q => q.FormId));
            var runtimeCount = 0;
            var stubCount = 0;
            foreach (var entry in _context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x47 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var quest = _context.RuntimeReader.ReadRuntimeQuest(entry);
                if (quest != null)
                {
                    quests.Add(quest);
                    runtimeCount++;
                }
                else
                {
                    // Runtime hash table confirms this is a quest (FormType 0x47),
                    // but the TESQuest struct is unreadable (corrupted/uncaptured memory).
                    quests.Add(new QuestRecord
                    {
                        FormId = entry.FormId,
                        EditorId = entry.EditorId,
                        FullName = _context.FormIdToFullName.GetValueOrDefault(entry.FormId),
                        Offset = 0,
                        IsBigEndian = true
                    });
                    stubCount++;
                }
            }

            if (runtimeCount > 0 || stubCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} quests from runtime struct reading " +
                    $"+ {stubCount} stubs (total: {quests.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return quests;
    }

    private QuestRecord? ReconstructQuestFromScanResult(DetectedMainRecord record)
    {
        return new QuestRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private QuestRecord? ReconstructQuestFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructQuestFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        byte flags = 0;
        byte priority = 0;
        float questDelay = 0;
        uint? script = null;
        var stages = new List<QuestStage>();
        var objectives = new List<QuestObjective>();

        // Track current stage/objective being built
        int? currentStageIndex = null;
        string? currentLogEntry = null;
        byte currentStageFlags = 0;
        int? currentObjectiveIndex = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 2:
                    flags = subData[0];
                    priority = subData[1];
                    if (sub.DataLength >= 8)
                    {
                        questDelay = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    }

                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INDX" when sub.DataLength >= 2:
                    // Save previous stage if any
                    if (currentStageIndex.HasValue)
                    {
                        stages.Add(new QuestStage
                        {
                            Index = currentStageIndex.Value,
                            LogEntry = currentLogEntry,
                            Flags = currentStageFlags
                        });
                    }

                    // Start new stage
                    currentStageIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    currentLogEntry = null;
                    currentStageFlags = 0;
                    break;
                case "CNAM": // Log entry text
                    currentLogEntry = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "QSDT" when sub.DataLength >= 1:
                    currentStageFlags = subData[0];
                    break;
                case "QOBJ" when sub.DataLength >= 4:
                    // Save previous objective if any
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value
                        });
                    }

                    currentObjectiveIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    break;
                case "NNAM": // Objective display text
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value,
                            DisplayText = EsmStringUtils.ReadNullTermString(subData)
                        });
                        currentObjectiveIndex = null;
                    }

                    break;
            }
        }

        // Add final stage if any
        if (currentStageIndex.HasValue)
        {
            stages.Add(new QuestStage
            {
                Index = currentStageIndex.Value,
                LogEntry = currentLogEntry,
                Flags = currentStageFlags
            });
        }

        // Add final objective if any
        if (currentObjectiveIndex.HasValue)
        {
            objectives.Add(new QuestObjective
            {
                Index = currentObjectiveIndex.Value
            });
        }

        return new QuestRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            QuestDelay = questDelay,
            Script = script,
            Stages = stages.OrderBy(s => s.Index).ToList(),
            Objectives = objectives.OrderBy(o => o.Index).ToList(),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Dialogue Linking and Merging

    /// <summary>
    ///     Enrich dialogue records with runtime TESTopicInfo data from the hash table.
    ///     Matches dialogue FormIDs against RuntimeEditorIds to find corresponding entries,
    ///     then reads the TESTopicInfo struct to get speaker, quest, flags, difficulty, and prompt.
    ///     Only enriches existing records - new entries are created by MergeRuntimeDialogueTopicLinks.
    /// </summary>
    internal void MergeRuntimeDialogueData(List<DialogueRecord> dialogues)
    {
        // Build FormID -> runtime entry lookup from hash table
        var runtimeByFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.TesFormOffset.HasValue)
            {
                runtimeByFormId.TryAdd(entry.FormId, entry);
            }
        }

        var mergedCount = 0;

        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            if (!runtimeByFormId.TryGetValue(dialogue.FormId, out var entry))
            {
                continue;
            }

            var runtimeInfo = _context.RuntimeReader!.ReadRuntimeDialogueInfo(entry);
            if (runtimeInfo == null)
            {
                continue;
            }

            dialogues[i] = dialogue with
            {
                EditorId = dialogue.EditorId ?? entry.EditorId ?? runtimeInfo.FormEditorId,
                PromptText = runtimeInfo.PromptText ?? dialogue.PromptText,
                InfoIndex = runtimeInfo.InfoIndex,
                InfoFlags = runtimeInfo.InfoFlags,
                InfoFlagsExt = runtimeInfo.InfoFlagsExt,
                Difficulty = runtimeInfo.Difficulty > 0 ? runtimeInfo.Difficulty : dialogue.Difficulty,
                SpeakerFormId = runtimeInfo.SpeakerFormId ?? dialogue.SpeakerFormId,
                QuestFormId = runtimeInfo.QuestFormId ?? dialogue.QuestFormId,
                SaidOnce = runtimeInfo.SaidOnce,
                AddTopics = runtimeInfo.AddTopicFormIds.Count > 0 && dialogue.AddTopics.Count == 0
                    ? runtimeInfo.AddTopicFormIds
                    : dialogue.AddTopics
            };
            mergedCount++;
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime INFO enrich: {mergedCount}/{dialogues.Count} enriched " +
            $"(hashEntries={runtimeByFormId.Count})");
    }

    /// <summary>
    ///     Walk TESTopic.m_listQuestInfo for each runtime DIAL entry to build
    ///     Topic -> Quest and Topic -> [INFO] mappings. Sets TopicFormId and QuestFormId
    ///     on all linked dialogue records.
    /// </summary>
    internal void MergeRuntimeDialogueTopicLinks(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        var dialFormType = DetectDialFormType(topics);
        if (!dialFormType.HasValue)
        {
            return;
        }

        var dialogueByFormId = BuildDialogueFormIdIndex(dialogues);
        var stats = new TopicLinkStats();

        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var questLinks = _context.RuntimeReader!.WalkTopicQuestInfoList(entry);
            if (questLinks.Count == 0)
            {
                continue;
            }

            stats.TopicsWalked++;
            stats.TotalInfosFound += questLinks.Sum(l => l.InfoEntries.Count);

            ProcessTopicQuestLinks(dialogues, dialogueByFormId, entry.FormId, questLinks, stats);
        }

        Logger.Instance.Debug(
            $"  [Semantic] Topic->Quest walk: {stats.TopicsWalked} topics, " +
            $"{stats.TotalInfosFound} INFO ptrs, {stats.TotalInfosLinked} existing linked, " +
            $"{stats.NewInfoCount} new INFOs created " +
            $"(+{stats.TopicLinked} TopicFormId, +{stats.QuestLinked} QuestFormId)");
    }

    /// <summary>
    ///     Detect the FormType byte used for DIAL records in the runtime editor ID table.
    /// </summary>
    private byte? DetectDialFormType(List<DialogTopicRecord> topics)
    {
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));
        var formTypeCounts = new Dictionary<byte, int>();

        foreach (var formType in _context.ScanResult.RuntimeEditorIds.Where(entry => knownDialFormIds.Contains(entry.FormId))
                     .Select(entry => entry.FormType))
        {
            formTypeCounts.TryGetValue(formType, out var count);
            formTypeCounts[formType] = count + 1;
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                return best.Key;
            }
        }

        // Fallback: use 0x45 (empirically verified shared DIAL+INFO FormType)
        const byte candidateFormType = 0x45;
        var hasEntries = _context.ScanResult.RuntimeEditorIds
            .Any(e => e.FormType == candidateFormType && e.TesFormOffset.HasValue);

        return hasEntries ? candidateFormType : null;
    }

    /// <summary>
    ///     Build a FormID -> list index lookup for dialogues.
    /// </summary>
    private static Dictionary<uint, int> BuildDialogueFormIdIndex(List<DialogueRecord> dialogues)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            index.TryAdd(dialogues[i].FormId, i);
        }

        return index;
    }

    /// <summary>
    ///     Process quest links for a single topic, updating or creating dialogue records.
    /// </summary>
    private void ProcessTopicQuestLinks(
        List<DialogueRecord> dialogues,
        Dictionary<uint, int> dialogueByFormId,
        uint topicFormId,
        List<TopicQuestLink> questLinks,
        TopicLinkStats stats)
    {
        foreach (var link in questLinks)
        {
            foreach (var infoEntry in link.InfoEntries)
            {
                if (dialogueByFormId.TryGetValue(infoEntry.FormId, out var idx))
                {
                    UpdateExistingDialogue(dialogues, idx, topicFormId, link.QuestFormId, stats);
                }
                else
                {
                    TryCreateNewDialogue(dialogues, dialogueByFormId, infoEntry, topicFormId, link.QuestFormId, stats);
                }
            }
        }
    }

    /// <summary>
    ///     Update an existing dialogue record with topic and quest FormIds if not already set.
    /// </summary>
    private static void UpdateExistingDialogue(
        List<DialogueRecord> dialogues,
        int index,
        uint topicFormId,
        uint questFormId,
        TopicLinkStats stats)
    {
        var existing = dialogues[index];
        var updated = existing;

        if (!existing.TopicFormId.HasValue || existing.TopicFormId.Value == 0)
        {
            updated = updated with { TopicFormId = topicFormId };
            stats.TopicLinked++;
        }

        if (!existing.QuestFormId.HasValue || existing.QuestFormId.Value == 0)
        {
            updated = updated with { QuestFormId = questFormId };
            stats.QuestLinked++;
        }

        if (updated != existing)
        {
            dialogues[index] = updated;
            stats.TotalInfosLinked++;
        }
    }

    /// <summary>
    ///     Create a new dialogue record from a runtime TESTopicInfo pointer.
    /// </summary>
    private void TryCreateNewDialogue(
        List<DialogueRecord> dialogues,
        Dictionary<uint, int> dialogueByFormId,
        InfoPointerEntry infoEntry,
        uint topicFormId,
        uint questFormId,
        TopicLinkStats stats)
    {
        var runtimeInfo = _context.RuntimeReader!.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
        if (runtimeInfo == null)
        {
            return;
        }

        var newDialogue = new DialogueRecord
        {
            FormId = infoEntry.FormId,
            EditorId = runtimeInfo.FormEditorId,
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            PromptText = runtimeInfo.PromptText,
            InfoIndex = runtimeInfo.InfoIndex,
            InfoFlags = runtimeInfo.InfoFlags,
            InfoFlagsExt = runtimeInfo.InfoFlagsExt,
            Difficulty = runtimeInfo.Difficulty,
            SpeakerFormId = runtimeInfo.SpeakerFormId,
            SaidOnce = runtimeInfo.SaidOnce,
            AddTopics = runtimeInfo.AddTopicFormIds,
            Offset = runtimeInfo.DumpOffset,
            IsBigEndian = true
        };

        dialogues.Add(newDialogue);
        dialogueByFormId.TryAdd(infoEntry.FormId, dialogues.Count - 1);
        stats.NewInfoCount++;
        stats.TopicLinked++;
        stats.QuestLinked++;
    }

    /// <summary>
    ///     Link dialogue records to quests by matching EditorID naming conventions.
    ///     Fallout NV INFO EditorIDs follow patterns like "{QuestPrefix}Topic{NNN}"
    ///     or "{QuestPrefix}{Speaker}Topic{NNN}". This is a heuristic fallback for
    ///     records not linked by the precise m_listQuestInfo walking.
    /// </summary>
    internal static void LinkDialogueByEditorIdConvention(
        List<DialogueRecord> dialogues,
        List<QuestRecord> quests)
    {
        // Build quest EditorID -> FormID index from the reconstructed quests list.
        // Quests already have EditorIDs from ESM scan + runtime merge.
        var questPrefixes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests)
        {
            if (!string.IsNullOrEmpty(quest.EditorId))
            {
                questPrefixes.TryAdd(quest.EditorId, quest.FormId);
            }
        }

        // Sort quest prefixes by length descending for longest-match-first
        var sortedPrefixes = questPrefixes
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

        var linked = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has QuestFormId
            if (dialogue.QuestFormId.HasValue && dialogue.QuestFormId.Value != 0)
            {
                continue;
            }

            // Skip if no EditorID to match
            if (string.IsNullOrEmpty(dialogue.EditorId))
            {
                continue;
            }

            // Find longest matching quest prefix
            foreach (var (prefix, questFormId) in sortedPrefixes)
            {
                if (dialogue.EditorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && dialogue.EditorId.Length > prefix.Length)
                {
                    dialogues[i] = dialogue with { QuestFormId = questFormId };
                    linked++;
                    break;
                }
            }
        }

        if (linked > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] EditorID convention matching: {linked} dialogues linked to quests " +
                $"({sortedPrefixes.Count} quest prefixes)");
        }
    }

    /// <summary>
    ///     Propagate topic-level speaker (TNAM) to INFO records that lack a speaker.
    ///     In Fallout NV, the speaker NPC is stored on the DIAL record's TNAM subrecord,
    ///     not per-INFO. This pass fills in SpeakerFormId for INFOs under topics with TNAM.
    /// </summary>
    internal static void PropagateTopicSpeakers(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> dialogTopics)
    {
        // Build TopicFormId -> SpeakerFormId map from topics that have TNAM
        var topicSpeakers = new Dictionary<uint, uint>();
        foreach (var topic in dialogTopics)
        {
            if (topic.SpeakerFormId.HasValue && topic.SpeakerFormId.Value != 0)
            {
                topicSpeakers.TryAdd(topic.FormId, topic.SpeakerFormId.Value);
            }
        }

        if (topicSpeakers.Count == 0)
        {
            return;
        }

        var propagated = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has a speaker
            if (dialogue.SpeakerFormId.HasValue && dialogue.SpeakerFormId.Value != 0)
            {
                continue;
            }

            // Skip if no topic link
            if (!dialogue.TopicFormId.HasValue || dialogue.TopicFormId.Value == 0)
            {
                continue;
            }

            // Look up topic-level speaker
            if (topicSpeakers.TryGetValue(dialogue.TopicFormId.Value, out var speakerFormId))
            {
                dialogues[i] = dialogue with { SpeakerFormId = speakerFormId };
                propagated++;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated topic-level speaker (TNAM) to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed siblings within the same topic.
    ///     If all attributed lines in a topic share the same speaker, unattributed lines inherit it.
    /// </summary>
    internal static void PropagateTopicSiblingSpeakers(List<DialogueRecord> dialogues)
    {
        var byTopic = new Dictionary<uint, List<int>>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            if (dialogues[i].TopicFormId is > 0)
            {
                var topicId = dialogues[i].TopicFormId!.Value;
                if (!byTopic.TryGetValue(topicId, out var list))
                {
                    list = [];
                    byTopic[topicId] = list;
                }

                list.Add(i);
            }
        }

        var propagated = 0;
        foreach (var (_, indices) in byTopic)
        {
            if (indices.Count < 2)
            {
                continue;
            }

            var attributedIndices = indices.Where(i => HasAnySpeaker(dialogues[i])).ToList();
            var unattributedIndices = indices.Where(i => !HasAnySpeaker(dialogues[i])).ToList();
            if (attributedIndices.Count == 0 || unattributedIndices.Count == 0)
            {
                continue;
            }

            // Try NPC speaker, then voice type, then faction
            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFormId, (d, v) => d with { SpeakerFormId = v }, out var count))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerVoiceTypeFormId, (d, v) => d with { SpeakerVoiceTypeFormId = v }, out count))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFactionFormId, (d, v) => d with { SpeakerFactionFormId = v }, out count))
            {
                propagated += count;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated topic-sibling speaker to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed lines within the same quest.
    ///     Uses a 60% threshold to avoid propagating in mixed-speaker quests.
    /// </summary>
    internal static void PropagateQuestSpeakers(List<DialogueRecord> dialogues)
    {
        var byQuest = new Dictionary<uint, List<int>>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            if (dialogues[i].QuestFormId is > 0)
            {
                var questId = dialogues[i].QuestFormId!.Value;
                if (!byQuest.TryGetValue(questId, out var list))
                {
                    list = [];
                    byQuest[questId] = list;
                }

                list.Add(i);
            }
        }

        var propagated = 0;
        foreach (var (_, indices) in byQuest)
        {
            var unattributedIndices = indices.Where(i => !HasAnySpeaker(dialogues[i])).ToList();
            if (unattributedIndices.Count == 0)
            {
                continue;
            }

            var attributedIndices = indices.Where(i => HasAnySpeaker(dialogues[i])).ToList();
            if (attributedIndices.Count == 0)
            {
                continue;
            }

            // Higher threshold (60%) for quest-level to avoid bad propagation
            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerVoiceTypeFormId, (d, v) => d with { SpeakerVoiceTypeFormId = v },
                    out var count, 0.6))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFactionFormId, (d, v) => d with { SpeakerFactionFormId = v },
                    out count, 0.6))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFormId, (d, v) => d with { SpeakerFormId = v },
                    out count, 0.6))
            {
                propagated += count;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated quest-level speaker to {propagated:N0} dialogue records");
        }
    }

    private static bool HasAnySpeaker(DialogueRecord d)
    {
        return d.SpeakerFormId is > 0 || d.SpeakerFactionFormId is > 0 ||
               d.SpeakerRaceFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0;
    }

    /// <summary>
    ///     Find the dominant (most common) value among attributed lines and propagate it to unattributed ones.
    ///     Returns true if propagation was performed.
    /// </summary>
    private static bool TryPropagateDominant(
        List<DialogueRecord> dialogues,
        List<int> attributedIndices,
        List<int> unattributedIndices,
        Func<DialogueRecord, uint?> selector,
        Func<DialogueRecord, uint, DialogueRecord> updater,
        out int propagatedCount,
        double minRatio = 0.5)
    {
        propagatedCount = 0;

        // Find the most common non-zero value
        var values = attributedIndices
            .Select(i => selector(dialogues[i]))
            .Where(v => v is > 0)
            .GroupBy(v => v!.Value)
            .Select(g => (Value: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        if (values.Count == 0)
        {
            return false;
        }

        var dominant = values[0];
        var total = attributedIndices.Count(i => selector(dialogues[i]) is > 0);
        if (total == 0 || (double)dominant.Count / total < minRatio)
        {
            return false;
        }

        // Propagate to unattributed lines
        foreach (var idx in unattributedIndices)
        {
            dialogues[idx] = updater(dialogues[idx], dominant.Value);
            propagatedCount++;
        }

        return propagatedCount > 0;
    }

    /// <summary>
    ///     Statistics for topic linking operations.
    /// </summary>
    private sealed class TopicLinkStats
    {
        public int NewInfoCount;
        public int QuestLinked;
        public int TopicLinked;
        public int TopicsWalked;
        public int TotalInfosFound;
        public int TotalInfosLinked;
    }

    /// <summary>
    ///     Link INFO records to their parent DIAL topics using the GRUP-based TopicToInfoMap.
    ///     The scanner builds this map from Type 7 GRUP headers which definitively encode
    ///     the DIAL->INFO parent-child relationship in the ESM file structure.
    ///     Falls back to file offset ordering if the map is empty (e.g., memory dump scans).
    /// </summary>
    internal void LinkInfoToTopicsByGroupOrder(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        var topicToInfoMap = _context.ScanResult.TopicToInfoMap;
        if (topicToInfoMap.Count == 0)
        {
            Logger.Instance.Debug("  [Semantic] No TopicToInfoMap available — skipping GRUP-based linking");
            return;
        }

        // Build FormID -> dialogue list index for updating
        var dialogueByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            dialogueByFormId.TryAdd(dialogues[i].FormId, i);
        }

        // Build DIAL FormID -> topic record for quest propagation
        var topicByFormId = new Dictionary<uint, DialogTopicRecord>();
        foreach (var topic in topics)
        {
            topicByFormId.TryAdd(topic.FormId, topic);
        }

        var linked = 0;
        var questLinked = 0;

        foreach (var (dialFormId, infoFormIds) in topicToInfoMap)
        {
            foreach (var infoFormId in infoFormIds)
            {
                if (!dialogueByFormId.TryGetValue(infoFormId, out var idx))
                {
                    continue;
                }

                var dialogue = dialogues[idx];
                var updated = dialogue;

                // Set TopicFormId if not already assigned
                if (!dialogue.TopicFormId.HasValue || dialogue.TopicFormId.Value == 0)
                {
                    updated = updated with { TopicFormId = dialFormId };
                    linked++;
                }

                // Propagate QuestFormId from the DIAL topic if the INFO lacks one
                if ((!dialogue.QuestFormId.HasValue || dialogue.QuestFormId.Value == 0)
                    && topicByFormId.TryGetValue(dialFormId, out var topic)
                    && topic.QuestFormId is > 0)
                {
                    updated = updated with { QuestFormId = topic.QuestFormId };
                    questLinked++;
                }

                if (updated != dialogue)
                {
                    dialogues[idx] = updated;
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] GRUP-based linking: linked {linked} INFOs to parent DIALs, " +
            $"{questLinked} quest propagations " +
            $"(from {topicToInfoMap.Count} topics, {dialogues.Count} INFOs)");
    }

    #endregion

    #region BuildDialogueTrees

    /// <summary>
    ///     Build hierarchical dialogue trees: Quest -> Topic -> INFO chains with cross-topic links.
    ///     Uses TopicFormId (from TPIC subrecord), QuestFormId (from QSTI or runtime), and
    ///     linking subrecords (TCLT/AddTopics) to build a navigable tree structure.
    /// </summary>
    internal DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        // Build indices
        var (infosByTopic, unlinkedInfos) = BuildInfosByTopicIndex(dialogues);
        var topicById = topics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var questById = quests
            .GroupBy(q => q.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Sort INFOs within each topic by InfoIndex
        foreach (var (_, infos) in infosByTopic)
        {
            infos.Sort((a, b) => a.InfoIndex.CompareTo(b.InfoIndex));
        }

        // Build TopicDialogueNode for each known topic
        var topicNodes = CreateTopicDialogueNodes(infosByTopic, topics, topicById);

        // Cross-link: fill in ChoiceTopics (TCLT) and AddedTopics (NAME) for each InfoDialogueNode
        CrossLinkInfoNodes(topicNodes);

        // Group topics by quest
        var (questTrees, orphanTopics) = GroupTopicsByQuest(topicNodes, questById);

        // Create orphan topic nodes for unlinked INFOs (no TopicFormId)
        CreateOrphanTopicNodes(unlinkedInfos, questTrees, orphanTopics, questById);

        // Sort topics within each quest by priority then name
        SortTopicsWithinQuests(questTrees);

        return new DialogueTreeResult
        {
            QuestTrees = questTrees,
            OrphanTopics = orphanTopics
        };
    }

    /// <summary>
    ///     Build an index of dialogues by their TopicFormId.
    /// </summary>
    private static (Dictionary<uint, List<DialogueRecord>> infosByTopic, List<DialogueRecord> unlinkedInfos)
        BuildInfosByTopicIndex(List<DialogueRecord> dialogues)
    {
        var infosByTopic = new Dictionary<uint, List<DialogueRecord>>();
        var unlinkedInfos = new List<DialogueRecord>();

        foreach (var d in dialogues)
        {
            if (d.TopicFormId.HasValue && d.TopicFormId.Value != 0)
            {
                if (!infosByTopic.TryGetValue(d.TopicFormId.Value, out var list))
                {
                    list = [];
                    infosByTopic[d.TopicFormId.Value] = list;
                }

                list.Add(d);
            }
            else
            {
                unlinkedInfos.Add(d);
            }
        }

        return (infosByTopic, unlinkedInfos);
    }

    /// <summary>
    ///     Create TopicDialogueNode for each known topic.
    /// </summary>
    private Dictionary<uint, TopicDialogueNode> CreateTopicDialogueNodes(
        Dictionary<uint, List<DialogueRecord>> infosByTopic,
        List<DialogTopicRecord> topics,
        Dictionary<uint, DialogTopicRecord> topicById)
    {
        var topicNodes = new Dictionary<uint, TopicDialogueNode>();

        // Include all topics that have INFOs or ESM DIAL records
        var allTopicIds = new HashSet<uint>(infosByTopic.Keys);
        foreach (var t in topics)
        {
            allTopicIds.Add(t.FormId);
        }

        foreach (var topicId in allTopicIds)
        {
            topicById.TryGetValue(topicId, out var topic);
            var topicName = topic?.FullName ?? topic?.EditorId ?? _context.ResolveFormName(topicId);

            var infos = infosByTopic.GetValueOrDefault(topicId, []);
            var infoNodes = infos.Select(info => new InfoDialogueNode
            {
                Info = info,
                ChoiceTopics = [],
                AddedTopics = []
            }).ToList();

            topicNodes[topicId] = new TopicDialogueNode
            {
                Topic = topic,
                TopicFormId = topicId,
                TopicName = topicName,
                InfoChain = infoNodes
            };
        }

        return topicNodes;
    }

    /// <summary>
    ///     Cross-link: fill in ChoiceTopics (TCLT) and AddedTopics (NAME) for each InfoDialogueNode.
    /// </summary>
    private static void CrossLinkInfoNodes(Dictionary<uint, TopicDialogueNode> topicNodes)
    {
        foreach (var (_, topicNode) in topicNodes)
        {
            foreach (var infoNode in topicNode.InfoChain)
            {
                foreach (var tcltId in infoNode.Info.LinkToTopics)
                {
                    if (topicNodes.TryGetValue(tcltId, out var choiceNode))
                    {
                        infoNode.ChoiceTopics.Add(choiceNode);
                    }
                }

                foreach (var addId in infoNode.Info.AddTopics)
                {
                    if (topicNodes.TryGetValue(addId, out var addedNode))
                    {
                        infoNode.AddedTopics.Add(addedNode);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Group topics by their associated quest.
    /// </summary>
    private (Dictionary<uint, QuestDialogueNode> questTrees, List<TopicDialogueNode> orphanTopics)
        GroupTopicsByQuest(
            Dictionary<uint, TopicDialogueNode> topicNodes,
            Dictionary<uint, QuestRecord> questById)
    {
        var questTrees = new Dictionary<uint, QuestDialogueNode>();
        var orphanTopics = new List<TopicDialogueNode>();

        foreach (var (_, topicNode) in topicNodes)
        {
            var questId = DetermineQuestIdForTopic(topicNode);

            if (questId.HasValue && questId.Value != 0)
            {
                var questNode = GetOrCreateQuestNode(questTrees, questId.Value, questById);
                questNode.Topics.Add(topicNode);
            }
            else
            {
                orphanTopics.Add(topicNode);
            }
        }

        return (questTrees, orphanTopics);
    }

    /// <summary>
    ///     Determine the quest FormId for a topic from its topic record or INFO records.
    /// </summary>
    private static uint? DetermineQuestIdForTopic(TopicDialogueNode topicNode)
    {
        var questId = topicNode.Topic?.QuestFormId;
        if (!questId.HasValue || questId.Value == 0)
        {
            questId = topicNode.InfoChain
                .Select(i => i.Info.QuestFormId)
                .FirstOrDefault(q => q.HasValue && q.Value != 0);
        }

        return questId;
    }

    /// <summary>
    ///     Get or create a QuestDialogueNode for the given quest FormId.
    /// </summary>
    private QuestDialogueNode GetOrCreateQuestNode(
        Dictionary<uint, QuestDialogueNode> questTrees,
        uint questId,
        Dictionary<uint, QuestRecord> questById)
    {
        if (!questTrees.TryGetValue(questId, out var questNode))
        {
            questById.TryGetValue(questId, out var quest);
            questNode = new QuestDialogueNode
            {
                QuestFormId = questId,
                QuestName = quest?.FullName ?? quest?.EditorId ?? _context.ResolveFormName(questId),
                Topics = []
            };
            questTrees[questId] = questNode;
        }

        return questNode;
    }

    /// <summary>
    ///     Create orphan topic nodes for unlinked INFOs (no TopicFormId).
    /// </summary>
    private void CreateOrphanTopicNodes(
        List<DialogueRecord> unlinkedInfos,
        Dictionary<uint, QuestDialogueNode> questTrees,
        List<TopicDialogueNode> orphanTopics,
        Dictionary<uint, QuestRecord> questById)
    {
        if (unlinkedInfos.Count == 0)
        {
            return;
        }

        // Group unlinked INFOs by quest, create synthetic topic nodes
        var unlinkedByQuest = unlinkedInfos
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in unlinkedByQuest)
        {
            var syntheticTopic = CreateSyntheticTopicNode(group);

            if (group.Key != 0)
            {
                var questNode = GetOrCreateQuestNode(questTrees, group.Key, questById);
                questNode.Topics.Add(syntheticTopic);
            }
            else
            {
                orphanTopics.Add(syntheticTopic);
            }
        }
    }

    /// <summary>
    ///     Create a synthetic topic node for a group of unlinked INFOs.
    /// </summary>
    private static TopicDialogueNode CreateSyntheticTopicNode(IGrouping<uint, DialogueRecord> group)
    {
        var infoNodes = group
            .OrderBy(d => d.InfoIndex)
            .ThenBy(d => d.EditorId ?? "")
            .Select(info => new InfoDialogueNode
            {
                Info = info,
                ChoiceTopics = [],
                AddedTopics = []
            }).ToList();

        return new TopicDialogueNode
        {
            Topic = null,
            TopicFormId = 0,
            TopicName = "(Unlinked Responses)",
            InfoChain = infoNodes
        };
    }

    /// <summary>
    ///     Sort topics within each quest by priority (descending) then by name.
    /// </summary>
    private static void SortTopicsWithinQuests(Dictionary<uint, QuestDialogueNode> questTrees)
    {
        foreach (var (_, questNode) in questTrees)
        {
            questNode.Topics.Sort((a, b) =>
            {
                var pa = a.Topic?.Priority ?? 0f;
                var pb = b.Topic?.Priority ?? 0f;
                var cmp = pb.CompareTo(pa); // Higher priority first
                return cmp != 0 ? cmp : string.Compare(a.TopicName, b.TopicName, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    #endregion
}
