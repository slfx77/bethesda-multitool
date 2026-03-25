using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Orchestrator for dialogue record parsing. Delegates to specialized classes:
///     <see cref="DialogueConditionParser" /> for INFO parsing and CTDA conditions,
///     <see cref="DialogueRuntimeMerger" /> for DMP runtime data merging,
///     <see cref="DialogueTopicMerger" /> for speaker propagation and linking,
///     <see cref="DialogueTreeBuilder" /> for building hierarchical dialogue trees.
/// </summary>
internal sealed class DialogueRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private readonly DialogueConditionParser _conditionParser = new(context);
    private readonly DialogueRuntimeMerger _runtimeMerger = new(context);
    private readonly DialogueTopicMerger _topicMerger = new(context);
    private readonly DialogueTreeBuilder _treeBuilder = new(context);

    #region ParseDialogue

    /// <summary>
    ///     Parse all Dialogue (INFO) records from the scan result.
    /// </summary>
    internal List<DialogueRecord> ParseDialogue()
    {
        var dialogues = _conditionParser.ParseAllInfoRecords();

        // Merge split INFO records -- Xbox 360 splits INFO into Base (conditions, links, speaker)
        // + Response (text, emotion) records with the same FormID. Also handles BE/LE duplicates.
        return DialogueConditionParser.MergeSplitInfoRecords(dialogues);
    }

    #endregion

    #region ParseDialogTopics

    /// <summary>
    ///     Parse all Dialog Topic records from the scan result.
    /// </summary>
    internal List<DialogTopicRecord> ParseDialogTopics()
    {
        var topics = new List<DialogTopicRecord>();
        var topicRecords = Context.GetRecordsByType("DIAL").ToList();

        if (Context.Accessor != null)
        {
            // Single-pass subrecord parsing for FULL, TNAM, QSTI, and DATA
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in topicRecords)
                {
                    var recordData = Context.ReadRecordData(record, buffer);
                    if (recordData == null)
                    {
                        // Fallback for unreadable records
                        topics.Add(new DialogTopicRecord
                        {
                            FormId = record.FormId,
                            EditorId = Context.GetEditorId(record.FormId),
                            FullName = Context.FindFullNameInRecordBounds(record),
                            Offset = record.Offset,
                            RawRecordOffset = record.Offset,
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
                    var priority = 0f;

                    foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                    {
                        var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
                        switch (sub.Signature)
                        {
                            case "EDID":
                            {
                                var edid = EsmStringUtils.ReadNullTermString(subData);
                                if (!string.IsNullOrEmpty(edid))
                                    Context.FormIdToEditorId[record.FormId] = edid;
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
                            case "PNAM" when sub.DataLength == 4:
                                priority = record.IsBigEndian
                                    ? BinaryPrimitives.ReadSingleBigEndian(subData)
                                    : BinaryPrimitives.ReadSingleLittleEndian(subData);
                                break;
                            case "DATA" when sub.DataLength >= 2:
                                // Topic type and flags -- raw bytes, no endian swap needed
                                topicType = subData[0];
                                topicFlags = subData[1];
                                break;
                        }
                    }

                    topics.Add(new DialogTopicRecord
                    {
                        FormId = record.FormId,
                        EditorId = Context.GetEditorId(record.FormId),
                        FullName = fullName,
                        SpeakerFormId = speakerFormId,
                        QuestFormId = questFormId,
                        TopicType = topicType,
                        Flags = topicFlags,
                        Priority = priority,
                        Offset = record.Offset,
                        RawRecordOffset = record.Offset,
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
                var fullName = Context.FindFullNameInRecordBounds(record);
                topics.Add(new DialogTopicRecord
                {
                    FormId = record.FormId,
                    EditorId = Context.GetEditorId(record.FormId),
                    FullName = fullName,
                    Offset = record.Offset,
                    RawRecordOffset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }

        // Deduplicate by FormID - same DIAL record can appear in both BE and LE memory regions.
        // Keep the version with the most data (prefer one with FullName, SpeakerFormId, and EditorId).
        var deduped = topics
            .GroupBy(t => t.FormId)
            .Select(g =>
            {
                var best = g
                    .OrderByDescending(t => t.SpeakerFormId.HasValue ? 1 : 0)
                    .ThenByDescending(t => t.FullName?.Length ?? 0)
                    .ThenByDescending(t => t.EditorId?.Length ?? 0)
                    .First();

                var rawRecordOffset = g
                    .Select(topic => topic.RawRecordOffset)
                    .Where(offset => offset > 0)
                    .DefaultIfEmpty(best.RawRecordOffset)
                    .Min();
                var runtimeStructOffset = g
                    .Select(topic => topic.RuntimeStructOffset)
                    .FirstOrDefault(offset => offset > 0);

                return best with
                {
                    RawRecordOffset = rawRecordOffset > 0 ? rawRecordOffset : best.RawRecordOffset,
                    RuntimeStructOffset = best.RuntimeStructOffset > 0
                        ? best.RuntimeStructOffset
                        : runtimeStructOffset
                };
            })
            .ToList();

        // Probe DIAL runtime struct layout and merge runtime data if reader available
        if (Context.RuntimeReader != null)
        {
            _runtimeMerger.MergeRuntimeDialogTopicData(deduped);
        }

        return deduped;
    }

    #endregion

    #region BuildDialogueTrees (Delegation)

    /// <summary>
    ///     Build hierarchical dialogue trees: Quest -> Topic -> INFO chains with cross-topic links.
    /// </summary>
    internal DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        return _treeBuilder.BuildDialogueTrees(dialogues, topics, quests);
    }

    #endregion

    #region ParseQuests

    /// <summary>
    ///     Parse all Quest records from the scan result.
    /// </summary>
    internal List<QuestRecord> ParseQuests()
    {
        var quests = new List<QuestRecord>();
        var questRecords = Context.GetRecordsByType("QUST").ToList();

        if (Context.Accessor == null)
        {
            foreach (var record in questRecords)
            {
                var quest = ParseQuestFromScanResult(record);
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
                    var quest = ParseQuestFromAccessor(record, buffer);
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
        if (Context.RuntimeReader != null)
        {
            var questByFormId = quests.ToDictionary(q => q.FormId);
            var runtimeCount = 0;
            var stubCount = 0;
            var enrichedCount = 0;
            var questEntryCount = 0;
            foreach (var entry in Context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x47)
                {
                    continue;
                }

                questEntryCount++;

                if (questByFormId.ContainsKey(entry.FormId))
                {
                    var existing = questByFormId[entry.FormId];
                    var needsEnrichment = NeedsRuntimeQuestEnrichment(existing);
                    if (needsEnrichment)
                    {
                        var runtimeQuest = Context.RuntimeReader.ReadRuntimeQuest(entry);
                        if (runtimeQuest != null)
                        {
                            var idx = quests.IndexOf(existing);
                            quests[idx] = MergeRuntimeQuest(existing, runtimeQuest, entry);
                            questByFormId[entry.FormId] = quests[idx];
                            enrichedCount++;
                        }
                    }

                    continue;
                }

                var quest = Context.RuntimeReader.ReadRuntimeQuest(entry);
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
                        FullName = Context.FormIdToFullName.GetValueOrDefault(entry.FormId),
                        Offset = 0,
                        IsBigEndian = true
                    });
                    stubCount++;
                }
            }

            Logger.Instance.Debug(
                $"Quest merge: {questEntryCount} runtime entries (FormType=0x47), " +
                $"added {runtimeCount} + {stubCount} stubs, enriched {enrichedCount} " +
                $"(total: {quests.Count}, ESM-scanned: {questByFormId.Count})");
        }

        return quests;
    }

    private QuestRecord? ParseQuestFromScanResult(DetectedMainRecord record)
    {
        return new QuestRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            FullName = Context.FindFullNameNear(record.Offset)
                       ?? Context.FormIdToFullName.GetValueOrDefault(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private QuestRecord? ParseQuestFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseQuestFromScanResult(record);
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
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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

    private static bool NeedsRuntimeQuestEnrichment(QuestRecord quest)
    {
        return string.IsNullOrEmpty(quest.FullName)
               || string.IsNullOrEmpty(quest.EditorId)
               || !quest.Script.HasValue
               || (quest.Flags == 0 && quest.Priority == 0 && MathF.Abs(quest.QuestDelay) < 0.0001f)
               || quest.Stages.Count == 0
               || quest.Stages.Any(stage => stage.Flags == 0)
               || quest.Objectives.Count == 0
               || quest.Objectives.Any(objective => string.IsNullOrEmpty(objective.DisplayText));
    }

    private static QuestRecord MergeRuntimeQuest(
        QuestRecord existing,
        QuestRecord runtimeQuest,
        RuntimeEditorIdEntry entry)
    {
        return existing with
        {
            EditorId = existing.EditorId ?? runtimeQuest.EditorId ?? entry.EditorId,
            FullName = string.IsNullOrEmpty(existing.FullName)
                ? runtimeQuest.FullName ?? entry.DisplayName
                : existing.FullName,
            Flags = existing.Flags != 0 ? existing.Flags : runtimeQuest.Flags,
            Priority = existing.Priority != 0 ? existing.Priority : runtimeQuest.Priority,
            QuestDelay = MathF.Abs(existing.QuestDelay) > 0.0001f
                ? existing.QuestDelay
                : runtimeQuest.QuestDelay,
            Script = existing.Script ?? runtimeQuest.Script,
            Stages = MergeQuestStages(existing.Stages, runtimeQuest.Stages),
            Objectives = MergeQuestObjectives(existing.Objectives, runtimeQuest.Objectives)
        };
    }

    private static List<QuestStage> MergeQuestStages(
        IReadOnlyList<QuestStage> esmStages,
        IReadOnlyList<QuestStage> runtimeStages)
    {
        if (runtimeStages.Count == 0)
        {
            return esmStages
                .OrderBy(stage => stage.Index)
                .ToList();
        }

        if (esmStages.Count == 0)
        {
            return runtimeStages
                .GroupBy(stage => stage.Index)
                .Select(group => group
                    .OrderByDescending(stage => stage.Flags != 0 ? 1 : 0)
                    .First())
                .OrderBy(stage => stage.Index)
                .ToList();
        }

        var merged = esmStages
            .GroupBy(stage => stage.Index)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(stage => stage.LogEntry?.Length ?? 0)
                    .ThenByDescending(stage => stage.Flags != 0 ? 1 : 0)
                    .First());

        foreach (var runtimeStage in runtimeStages)
        {
            if (merged.TryGetValue(runtimeStage.Index, out var existingStage))
            {
                merged[runtimeStage.Index] = existingStage with
                {
                    LogEntry = string.IsNullOrEmpty(existingStage.LogEntry)
                        ? runtimeStage.LogEntry
                        : existingStage.LogEntry,
                    Flags = existingStage.Flags != 0
                        ? existingStage.Flags
                        : runtimeStage.Flags
                };
            }
            else
            {
                merged[runtimeStage.Index] = runtimeStage;
            }
        }

        return merged
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToList();
    }

    private static List<QuestObjective> MergeQuestObjectives(
        IReadOnlyList<QuestObjective> esmObjectives,
        IReadOnlyList<QuestObjective> runtimeObjectives)
    {
        if (runtimeObjectives.Count == 0)
        {
            return esmObjectives
                .OrderBy(objective => objective.Index)
                .ToList();
        }

        if (esmObjectives.Count == 0)
        {
            return runtimeObjectives
                .GroupBy(objective => objective.Index)
                .Select(group => group
                    .OrderByDescending(objective => objective.DisplayText?.Length ?? 0)
                    .First())
                .OrderBy(objective => objective.Index)
                .ToList();
        }

        var merged = esmObjectives
            .GroupBy(objective => objective.Index)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(objective => objective.DisplayText?.Length ?? 0)
                    .First());

        foreach (var runtimeObjective in runtimeObjectives)
        {
            if (merged.TryGetValue(runtimeObjective.Index, out var existingObjective))
            {
                merged[runtimeObjective.Index] = existingObjective with
                {
                    DisplayText = string.IsNullOrEmpty(existingObjective.DisplayText)
                        ? runtimeObjective.DisplayText
                        : existingObjective.DisplayText,
                    TargetStage = existingObjective.TargetStage ?? runtimeObjective.TargetStage
                };
            }
            else
            {
                merged[runtimeObjective.Index] = runtimeObjective;
            }
        }

        return merged
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToList();
    }

    #endregion

    #region Dialogue Linking and Merging (Delegation)

    /// <summary>
    ///     Enrich dialogue records with runtime TESTopicInfo data from the hash table.
    /// </summary>
    internal void MergeRuntimeDialogueData(List<DialogueRecord> dialogues)
    {
        _runtimeMerger.MergeRuntimeDialogueData(dialogues);
    }

    /// <summary>
    ///     Walk TESTopic.m_listQuestInfo for each runtime DIAL entry to build
    ///     Topic -> Quest and Topic -> [INFO] mappings.
    /// </summary>
    internal void MergeRuntimeDialogueTopicLinks(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        _runtimeMerger.MergeRuntimeDialogueTopicLinks(dialogues, topics);
    }

    /// <summary>
    ///     Link dialogue records to quests by matching EditorID naming conventions.
    /// </summary>
    internal static void LinkDialogueByEditorIdConvention(
        List<DialogueRecord> dialogues,
        List<QuestRecord> quests)
    {
        DialogueTopicMerger.LinkDialogueByEditorIdConvention(dialogues, quests);
    }

    /// <summary>
    ///     Propagate topic-level speaker (TNAM) to INFO records that lack a speaker.
    /// </summary>
    internal static void PropagateTopicSpeakers(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> dialogTopics)
    {
        DialogueTopicMerger.PropagateTopicSpeakers(dialogues, dialogTopics);
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed siblings within the same topic.
    /// </summary>
    internal static void PropagateTopicSiblingSpeakers(List<DialogueRecord> dialogues)
    {
        DialogueTopicMerger.PropagateTopicSiblingSpeakers(dialogues);
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed lines within the same quest.
    /// </summary>
    internal static void PropagateQuestSpeakers(List<DialogueRecord> dialogues)
    {
        DialogueTopicMerger.PropagateQuestSpeakers(dialogues);
    }

    /// <summary>
    ///     Link INFO records to their parent DIAL topics using the GRUP-based TopicToInfoMap.
    /// </summary>
    internal void LinkInfoToTopicsByGroupOrder(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        _topicMerger.LinkInfoToTopicsByGroupOrder(dialogues, topics);
    }

    #endregion
}
