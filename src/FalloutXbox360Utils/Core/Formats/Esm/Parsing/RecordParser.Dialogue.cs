using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RecordParser
{
    #region ReconstructDialogue

    /// <summary>
    ///     Reconstruct all Dialogue (INFO) records from the scan result.
    /// </summary>
    public List<DialogueRecord> ReconstructDialogue()
    {
        var dialogues = new List<DialogueRecord>();
        var infoRecords = GetRecordsByType("INFO").ToList();

        if (_accessor == null)
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

        // Deduplicate by FormID - same record can appear in both BE and LE memory regions.
        // Keep the version with the most response data.
        var deduped = dialogues
            .GroupBy(d => d.FormId)
            .Select(g => g.OrderByDescending(d => d.Responses.Count)
                .ThenByDescending(d => d.Responses.Sum(r => r.Text?.Length ?? 0))
                .First())
            .ToList();

        return deduped;
    }

    private DialogueRecord? ReconstructDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find response texts strictly within this INFO record's data bounds
        var dataStart = record.Offset + 24; // Skip main record header
        var dataEnd = dataStart + record.DataSize;
        var responseTexts = _scanResult.ResponseTexts
            .Where(r => r.Offset >= dataStart && r.Offset < dataEnd)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private DialogueRecord? ReconstructDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
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
        var responses = new List<DialogueResponse>();
        var linkToTopics = new List<uint>();
        var linkFromTopics = new List<uint>();
        var addTopics = new List<uint>();

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
                    break;
                case "QSTI" when sub.DataLength == 4:
                    questFormId = ReadFormId(subData, record.IsBigEndian);
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
                    previousInfo = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ANAM" when sub.DataLength == 4:
                    speakerFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPIC" when sub.DataLength == 4:
                    topicFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TCLT" when sub.DataLength == 4:
                    {
                        var tcltFormId = ReadFormId(subData, record.IsBigEndian);
                        if (tcltFormId != 0)
                        {
                            linkToTopics.Add(tcltFormId);
                        }

                        break;
                    }
                case "TCLF" when sub.DataLength == 4:
                    {
                        var tclfFormId = ReadFormId(subData, record.IsBigEndian);
                        if (tclfFormId != 0)
                        {
                            linkFromTopics.Add(tclfFormId);
                        }

                        break;
                    }
                case "NAME" when sub.DataLength == 4:
                    {
                        var nameFormId = ReadFormId(subData, record.IsBigEndian);
                        if (nameFormId != 0)
                        {
                            addTopics.Add(nameFormId);
                        }

                        break;
                    }
                case "DNAM" when sub.DataLength >= 4:
                    difficulty = ReadFormId(subData, record.IsBigEndian);
                    if (difficulty > 10)
                    {
                        difficulty = 0;
                    }

                    break;
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
            EditorId = editorId ?? GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            SpeakerFormId = speakerFormId,
            Responses = responses,
            PreviousInfo = previousInfo,
            Difficulty = difficulty,
            LinkToTopics = linkToTopics,
            LinkFromTopics = linkFromTopics,
            AddTopics = addTopics,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructDialogTopics

    /// <summary>
    ///     Reconstruct all Dialog Topic records from the scan result.
    /// </summary>
    public List<DialogTopicRecord> ReconstructDialogTopics()
    {
        var topics = new List<DialogTopicRecord>();
        var topicRecords = GetRecordsByType("DIAL").ToList();

        if (_accessor != null)
        {
            // Use accessor-based subrecord parsing to find FULL and TNAM within DIAL record bounds
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in topicRecords)
                {
                    var fullName = FindFullNameInRecord(record, buffer);
                    var speakerFormId = FindFormIdSubrecordInRecord(record, buffer, "TNAM");
                    topics.Add(new DialogTopicRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = fullName,
                        SpeakerFormId = speakerFormId,
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
                var fullName = FindFullNameInRecordBounds(record);
                topics.Add(new DialogTopicRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
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
        if (_runtimeReader != null)
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
        foreach (var formType in _scanResult.RuntimeEditorIds.Where(entry => knownDialFormIds.Contains(entry.FormId)).Select(entry => entry.FormType))
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
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != candidateFormType || !entry.TesFormOffset.HasValue)
                {
                    continue;
                }

                if (++testedCount > 20)
                {
                    break;
                }

                var probe = _runtimeReader!.ReadRuntimeDialogTopic(entry);
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

        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var runtimeTopic = _runtimeReader!.ReadRuntimeDialogTopic(entry);
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
    public List<QuestRecord> ReconstructQuests()
    {
        var quests = new List<QuestRecord>();
        var questRecords = GetRecordsByType("QUST").ToList();

        if (_accessor == null)
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
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(quests.Select(q => q.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x47 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var quest = _runtimeReader.ReadRuntimeQuest(entry);
                if (quest != null)
                {
                    quests.Add(quest);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} quests from runtime struct reading " +
                    $"(total: {quests.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return quests;
    }

    private QuestRecord? ReconstructQuestFromScanResult(DetectedMainRecord record)
    {
        return new QuestRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private QuestRecord? ReconstructQuestFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructQuestFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        byte flags = 0;
        byte priority = 0;
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
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
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
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            Priority = priority,
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
    private void MergeRuntimeDialogueData(List<DialogueRecord> dialogues)
    {
        // Build FormID -> runtime entry lookup from hash table
        var runtimeByFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
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

            var runtimeInfo = _runtimeReader!.ReadRuntimeDialogueInfo(entry);
            if (runtimeInfo == null)
            {
                continue;
            }

            dialogues[i] = dialogue with
            {
                EditorId = dialogue.EditorId ?? entry.EditorId,
                PromptText = runtimeInfo.PromptText ?? dialogue.PromptText,
                InfoIndex = runtimeInfo.InfoIndex,
                InfoFlags = runtimeInfo.InfoFlags,
                InfoFlagsExt = runtimeInfo.InfoFlagsExt,
                Difficulty = runtimeInfo.Difficulty > 0 ? runtimeInfo.Difficulty : dialogue.Difficulty,
                SpeakerFormId = runtimeInfo.SpeakerFormId ?? dialogue.SpeakerFormId,
                QuestFormId = runtimeInfo.QuestFormId ?? dialogue.QuestFormId
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
    private void MergeRuntimeDialogueTopicLinks(
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

        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var questLinks = _runtimeReader!.WalkTopicQuestInfoList(entry);
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

        foreach (var formType in _scanResult.RuntimeEditorIds.Where(entry => knownDialFormIds.Contains(entry.FormId)).Select(entry => entry.FormType))
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
        var hasEntries = _scanResult.RuntimeEditorIds
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
        var runtimeInfo = _runtimeReader!.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
        if (runtimeInfo == null)
        {
            return;
        }

        var newDialogue = new DialogueRecord
        {
            FormId = infoEntry.FormId,
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            PromptText = runtimeInfo.PromptText,
            InfoIndex = runtimeInfo.InfoIndex,
            InfoFlags = runtimeInfo.InfoFlags,
            InfoFlagsExt = runtimeInfo.InfoFlagsExt,
            Difficulty = runtimeInfo.Difficulty,
            SpeakerFormId = runtimeInfo.SpeakerFormId,
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
    private static void LinkDialogueByEditorIdConvention(
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

        Logger.Instance.Debug(
            $"  [Semantic] EditorID convention matching: {linked} dialogues linked to quests " +
            $"({sortedPrefixes.Count} quest prefixes)");
    }

    /// <summary>
    ///     Propagate topic-level speaker (TNAM) to INFO records that lack a speaker.
    ///     In Fallout NV, the speaker NPC is stored on the DIAL record's TNAM subrecord,
    ///     not per-INFO. This pass fills in SpeakerFormId for INFOs under topics with TNAM.
    /// </summary>
    private static void PropagateTopicSpeakers(
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

    #endregion

    #region BuildDialogueTrees

    /// <summary>
    ///     Build hierarchical dialogue trees: Quest -> Topic -> INFO chains with cross-topic links.
    ///     Uses TopicFormId (from TPIC subrecord), QuestFormId (from QSTI or runtime), and
    ///     linking subrecords (TCLT/AddTopics) to build a navigable tree structure.
    /// </summary>
    public DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        // Build indices
        var (infosByTopic, unlinkedInfos) = BuildInfosByTopicIndex(dialogues);
        var topicById = topics.ToDictionary(t => t.FormId, t => t);
        var questById = quests.ToDictionary(q => q.FormId, q => q);

        // Sort INFOs within each topic by InfoIndex
        foreach (var (_, infos) in infosByTopic)
        {
            infos.Sort((a, b) => a.InfoIndex.CompareTo(b.InfoIndex));
        }

        // Build TopicDialogueNode for each known topic
        var topicNodes = CreateTopicDialogueNodes(infosByTopic, topics, topicById);

        // Cross-link: fill in LinkedTopics for each InfoDialogueNode
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
            var topicName = topic?.FullName ?? topic?.EditorId ?? ResolveFormName(topicId);

            var infos = infosByTopic.GetValueOrDefault(topicId, []);
            var infoNodes = infos.Select(info => new InfoDialogueNode
            {
                Info = info,
                LinkedTopics = []
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
    ///     Cross-link: fill in LinkedTopics for each InfoDialogueNode.
    /// </summary>
    private static void CrossLinkInfoNodes(Dictionary<uint, TopicDialogueNode> topicNodes)
    {
        foreach (var (_, topicNode) in topicNodes)
        {
            foreach (var infoNode in topicNode.InfoChain)
            {
                var linkedIds = new HashSet<uint>(infoNode.Info.LinkToTopics);
                foreach (var addId in infoNode.Info.AddTopics)
                {
                    linkedIds.Add(addId);
                }

                foreach (var linkedId in linkedIds)
                {
                    if (topicNodes.TryGetValue(linkedId, out var linkedNode))
                    {
                        infoNode.LinkedTopics.Add(linkedNode);
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
                QuestName = quest?.FullName ?? quest?.EditorId ?? ResolveFormName(questId),
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
                LinkedTopics = []
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
